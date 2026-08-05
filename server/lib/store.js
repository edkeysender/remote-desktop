// JSON-file data store for the multi-tenant control plane. One file, loaded into
// memory, written synchronously on change (fine at self-hosted scale). Entities:
//
//   orgs      : { id, name, createdAt }
//   users     : { id, orgId, email, name, passHash, role: 'admin'|'user', createdAt }
//   invites   : { token, orgId, email, role, invitedBy, createdAt, acceptedBy? }
//   groups    : { id, orgId, name }
//   computers : keyed by device token → { orgId, name, groupId, lastSeen, relayId }
//   memberships (user→groups) is stored on each user as user.groupIds[]
//
// Access rule: a user may connect to a computer iff same org AND (user is admin OR
// the computer's group is one of the user's groups).

import { randomBytes, createHash, createHmac } from 'crypto';
import { readFileSync, writeFileSync, existsSync, mkdirSync, renameSync, chmodSync } from 'fs';
import { resolve, join } from 'path';

const DATA_DIR = resolve(process.env.DATA_DIR || './data');
const DB_FILE = join(DATA_DIR, 'db.json');

const empty = () => ({
  orgs: {}, users: {}, invites: {}, groups: {}, computers: {}, enrollTokens: {},
  events: [], sessions: [],
});
const MAX_EVENTS = 5000, MAX_SESSIONS = 2000;
let db = empty();

function load() {
  try { if (existsSync(DB_FILE)) db = { ...empty(), ...JSON.parse(readFileSync(DB_FILE, 'utf8')) }; }
  catch (e) { console.error('[store] load failed:', e.message); }
}
// This file holds password hashes, API/enrollment/invite tokens and the org's TURN
// password — it must not be world-readable. Write to a temp file and rename so a crash
// mid-write can never truncate the database.
function save() {
  try {
    mkdirSync(DATA_DIR, { recursive: true, mode: 0o700 });
    const tmp = DB_FILE + '.tmp';
    writeFileSync(tmp, JSON.stringify(db, null, 2), { mode: 0o600 });
    renameSync(tmp, DB_FILE);
    chmodSync(DB_FILE, 0o600);   // rename preserves the temp file's mode; be explicit anyway
  } catch (e) { console.error('[store] save failed:', e.message); }
}
load();

const id = (p) => p + randomBytes(6).toString('hex');
const now = () => Date.now();
const sha256 = (s) => createHash('sha256').update(String(s)).digest('hex');

const API_TOKEN_TTL_MS = 365 * 24 * 60 * 60 * 1000;   // 1 year
const ENROLL_TTL_MS = 90 * 24 * 60 * 60 * 1000;       // 90 days
const MIGRATION_GRACE_MS = 30 * 24 * 60 * 60 * 1000;  // never expire an existing token instantly

// Bring older on-disk data up to the current shape. Runs once at boot, idempotent.
function migrate() {
  let dirty = false;
  for (const o of Object.values(db.orgs)) {
    if (!o.apiTokens) continue;
    for (const [key, v] of Object.entries(o.apiTokens)) {
      if (v.hash) continue;                      // already migrated
      // The plaintext token was the map key. Re-key by hash so a database read no longer
      // yields usable credentials; keep a prefix so the UI can still identify the row.
      delete o.apiTokens[key];
      o.apiTokens[sha256(key)] = {
        id: id('at_'), label: v.label || '', prefix: String(key).slice(0, 11),
        createdAt: v.createdAt || now(), hash: true,
        expiresAt: Math.max((v.createdAt || now()) + API_TOKEN_TTL_MS, now() + MIGRATION_GRACE_MS),
      };
      dirty = true;
    }
  }
  for (const e of Object.values(db.enrollTokens)) {
    if (e.expiresAt) continue;
    e.expiresAt = Math.max((e.createdAt || now()) + ENROLL_TTL_MS, now() + MIGRATION_GRACE_MS);
    dirty = true;
  }
  if (dirty) save();
}
migrate();

// Role model (plan §1). 'owner' is the org creator (immutable, full control).
// Assignable roles, most→least privilege:
//   admin      — manage org (users, groups, devices, tokens); connect to all devices
//   technician — connect + full session tools on devices in their groups
//   operator   — connect (view/control) devices in their groups
//   viewer     — read-only: dashboards, sessions, audit; cannot connect
export const ROLES = ['admin', 'technician', 'operator', 'viewer'];
const normRole = (r) => (ROLES.includes(r) ? r : 'operator');
export const isManager = (user) => user && (user.role === 'owner' || user.role === 'admin');
export const canAudit = (user) => isManager(user) || user?.role === 'viewer';

// ------------------------------- audit log & sessions -------------------------------

/** Append an audit event (org-scoped). type e.g. 'login','session.start','user.role'. */
export function logEvent(orgId, type, { actorEmail = null, target = null, detail = null } = {}) {
  if (!orgId) return;
  db.events.push({ id: id('ev_'), orgId, ts: now(), type, actorEmail, target, detail });
  if (db.events.length > MAX_EVENTS) db.events.splice(0, db.events.length - MAX_EVENTS);
  save();
}

export function listEvents(orgId, limit = 200) {
  return db.events.filter((e) => e.orgId === orgId).slice(-limit).reverse();
}

/** Open a session record when a viewer is paired to a host. Returns its id. */
export function startSession({ orgId, deviceToken, relayId, computerName, viewerEmail, mode }) {
  const s = {
    id: id('ses_'), orgId, deviceToken, relayId,
    computerName: computerName || null, viewerEmail: viewerEmail || null,
    mode: mode || 'remote', startedAt: now(), endedAt: null,
  };
  db.sessions.push(s);
  if (db.sessions.length > MAX_SESSIONS) db.sessions.splice(0, db.sessions.length - MAX_SESSIONS);
  save();
  return s.id;
}

export function endSession(sessionId) {
  const s = db.sessions.find((x) => x.id === sessionId);
  if (s && !s.endedAt) { s.endedAt = now(); save(); }
}

export function listSessions(orgId, limit = 200) {
  return db.sessions.filter((s) => s.orgId === orgId).slice(-limit).reverse();
}

/** On boot, no session can still be live — close any left open by a previous run. */
export function closeOpenSessions() {
  let changed = false;
  for (const s of db.sessions) if (!s.endedAt) { s.endedAt = now(); changed = true; }
  if (changed) save();
}

// ------------------------------- orgs / users -------------------------------

export function emailTaken(email) {
  email = email.toLowerCase();
  return Object.values(db.users).some((u) => u.email === email);
}

export function findUserByEmail(email) {
  email = email.toLowerCase();
  return Object.values(db.users).find((u) => u.email === email) || null;
}

export function getUser(userId) { return db.users[userId] || null; }
export function getOrg(orgId) { return db.orgs[orgId] || null; }

// ---- session revocation ----
// Session tokens are stateless HMACs, so there is no server-side list to delete from.
// Instead each user carries a watermark: any token issued before it is refused. Signing
// out, or an admin cutting someone off, moves the watermark and kills every outstanding
// token for that user immediately. See auth.verifyToken.
export function revokeSessions(userId) {
  const u = db.users[userId];
  if (!u) return;
  u.tokenEpoch = now();
  save();
}
export function sessionsValidFrom(userId) { return db.users[userId]?.tokenEpoch || 0; }

// ---- org API tokens (for the MCP server / integrations; manager-scoped) ----
// Stored keyed by SHA-256 of the token, never in the clear: these grant org-admin, so a
// read of db.json must not hand them over. The plaintext is returned exactly once, at
// creation; afterwards only a prefix is shown so a row stays identifiable.
export function createApiToken(orgId, label) {
  const o = db.orgs[orgId]; if (!o) return null;
  o.apiTokens = o.apiTokens || {};
  const token = 'hk_' + randomBytes(24).toString('hex');
  const rec = {
    id: id('at_'), label: (label || '').toString().slice(0, 60), prefix: token.slice(0, 11),
    createdAt: now(), expiresAt: now() + API_TOKEN_TTL_MS, hash: true,
  };
  o.apiTokens[sha256(token)] = rec;
  save();
  return { token, id: rec.id, label: rec.label, prefix: rec.prefix, expiresAt: rec.expiresAt };
}
export function listApiTokens(orgId) {
  const o = db.orgs[orgId];
  if (!o?.apiTokens) return [];
  return Object.values(o.apiTokens)
    .map((v) => ({ id: v.id, label: v.label, prefix: v.prefix, createdAt: v.createdAt, expiresAt: v.expiresAt }));
}
export function revokeApiToken(orgId, tokenId) {
  const o = db.orgs[orgId];
  if (!o?.apiTokens) return;
  for (const [k, v] of Object.entries(o.apiTokens)) {
    if (v.id === tokenId) { delete o.apiTokens[k]; save(); return; }
  }
}
export function resolveApiToken(token) {
  const h = sha256(token);
  for (const [orgId, o] of Object.entries(db.orgs)) {
    const rec = o.apiTokens?.[h];
    if (!rec) continue;
    if (rec.expiresAt && rec.expiresAt < now()) return null;
    return orgId;
  }
  return null;
}

// ---- platform (super-admin) views ----
export const PLANS = ['trial', 'cloud', 'self-hosted'];
export function setOrgPlan(orgId, plan) {
  const o = db.orgs[orgId];
  if (o && PLANS.includes(plan)) { o.plan = plan; save(); }
  return o;
}
// ---- device configurations (per-org, applied per device) ----
// A config declares desired state; the host checks/applies it and reports results.
// The list of checks grows over time; unknown fields are ignored by older hosts.
export function listConfigs(orgId) { return db.orgs[orgId]?.configs || []; }
export function getConfig(orgId, id) { return (db.orgs[orgId]?.configs || []).find((c) => c.id === id) || null; }
export function createConfig(orgId, name) {
  const o = db.orgs[orgId]; if (!o) return null;
  o.configs = o.configs || [];
  const c = {
    id: 'cfg_' + randomBytes(4).toString('hex'),
    name: (name || 'Configuration').toString().trim().slice(0, 60) || 'Configuration',
    wallpaper: null, loginBackground: null,
    checkWindowsActivated: false, installVcredist: false, installOpenSSH: false,
    computerNameStandard: '',
  };
  o.configs.push(c); save(); return c;
}
export function updateConfig(orgId, id, f = {}) {
  const c = getConfig(orgId, id); if (!c) return null;
  if (typeof f.name === 'string') c.name = f.name.trim().slice(0, 60) || c.name;
  for (const k of ['checkWindowsActivated', 'installVcredist', 'installOpenSSH']) if (k in f) c[k] = !!f[k];
  if ('computerNameStandard' in f) c.computerNameStandard = (f.computerNameStandard || '').toString().slice(0, 120);
  for (const k of ['wallpaper', 'loginBackground']) if (k in f) {
    const v = f[k];
    c[k] = (typeof v === 'string' && v.startsWith('data:image/') && v.length < 800_000) ? v : (v === null ? null : c[k]);
  }
  save(); return c;
}
export function deleteConfig(orgId, id) {
  const o = db.orgs[orgId]; if (!o) return;
  o.configs = (o.configs || []).filter((c) => c.id !== id);
  for (const c of Object.values(db.computers)) if (c.configId === id) c.configId = null;
  save();
}
export function setComputerConfig(deviceToken, orgId, configId) {
  const c = db.computers[deviceToken];
  if (!c || c.orgId !== orgId) return null;
  c.configId = configId && getConfig(orgId, configId) ? configId : null;
  save(); return c;
}

// ---- per-org network / ICE settings (STUN/TURN for WebRTC) ----
// What peers receive resolves in two layers:
//   1. the org's saved settings (panel → Configurations → Network / relay), else
//   2. server-wide defaults from the environment:
//        STUN_URLS    comma-separated STUN URIs (default: Google's public server)
//        TURN_URL     e.g. turn:remotler.com:3478
//        TURN_USER / TURN_PASS   static long-term credential (coturn lt-cred-mech), or
//        TURN_SECRET  coturn `use-auth-secret` shared secret — a short-lived credential
//                     is minted per call (TURN REST API, username = expiry timestamp,
//                     password = HMAC-SHA1), so viewers never hold anything long-lived.
// Without a TURN fallback, any browser/host pair that can't STUN-hole-punch (symmetric
// NAT, UDP-filtered networks) fails ICE outright — "no network path to the device".
// An org that saved an explicitly-empty STUN list opted into LAN-only/no-external;
// the env TURN default is not injected for it.
const TURN_CRED_TTL_S = 24 * 60 * 60;

function envStun() {
  const s = (process.env.STUN_URLS || '').split(',').map((x) => x.trim()).filter(Boolean);
  return s.length ? s : ['stun:stun.l.google.com:19302'];
}
function envTurn() {
  const turnUrl = (process.env.TURN_URL || '').trim();
  if (!turnUrl) return null;
  const secret = (process.env.TURN_SECRET || '').trim();
  if (secret) {
    const turnUser = String(Math.floor(Date.now() / 1000) + TURN_CRED_TTL_S);
    const turnPass = createHmac('sha1', secret).update(turnUser).digest('base64');
    return { turnUrl, turnUser, turnPass };
  }
  return {
    turnUrl,
    turnUser: (process.env.TURN_USER || '').trim() || null,
    turnPass: (process.env.TURN_PASS || '').trim() || null,
  };
}

export function getIce(orgId) {
  const i = db.orgs[orgId]?.ice;
  const stun = i && Array.isArray(i.stun) ? i.stun : envStun();
  const lanOnly = !!i && Array.isArray(i.stun) && i.stun.length === 0;
  const turn = i?.turnUrl
    ? { turnUrl: i.turnUrl, turnUser: i.turnUser || null, turnPass: i.turnPass || null }
    : (lanOnly ? null : envTurn());
  return { stun, turnUrl: null, turnUser: null, turnPass: null, ...turn };
}

// What the panel edits: the org's stored row only. Env-minted TURN credentials must
// never round-trip through the form and get saved back as if they were org settings.
// `serverTurn` lets the UI say a server-wide TURN default is active when the org
// fields are blank.
export function getIceStored(orgId) {
  const i = db.orgs[orgId]?.ice;
  return {
    stun: i && Array.isArray(i.stun) ? i.stun : envStun(),
    turnUrl: i?.turnUrl || null, turnUser: i?.turnUser || null, turnPass: i?.turnPass || null,
    serverTurn: !!(process.env.TURN_URL || '').trim(),
  };
}
export function setIce(orgId, { stun, turnUrl, turnUser, turnPass } = {}) {
  const o = db.orgs[orgId]; if (!o) return null;
  let s;
  if (Array.isArray(stun)) s = stun.map((x) => (x || '').toString().trim()).filter(Boolean);
  else if (typeof stun === 'string') s = stun.split(',').map((x) => x.trim()).filter(Boolean);
  o.ice = {
    stun: s !== undefined ? s : (o.ice?.stun || ['stun:stun.l.google.com:19302']),
    turnUrl: (turnUrl || '').toString().trim() || null,
    turnUser: (turnUser || '').toString().trim() || null,
    turnPass: (turnPass || '').toString().trim() || null,
  };
  save();
  return getIceStored(orgId);
}

// ---- per-org branding (white-label) ----
export function getBranding(orgId) {
  const b = db.orgs[orgId]?.branding || {};
  return { appName: b.appName || 'Remotler', accent: b.accent || '#5B5BF5', logo: b.logo || null };
}
export function setBranding(orgId, { appName, accent, logo } = {}) {
  const o = db.orgs[orgId];
  if (!o) return null;
  const prev = o.branding || {};
  o.branding = {
    appName: (appName || '').toString().trim().slice(0, 40) || 'Remotler',
    accent: /^#[0-9a-fA-F]{6}$/.test(accent || '') ? accent : (prev.accent || '#5B5BF5'),
    // logo is a small data: URL (PNG/SVG). Cap size; keep previous if omitted/invalid.
    logo: typeof logo === 'string' && logo.startsWith('data:image/') && logo.length < 400_000
      ? logo : (logo === null ? null : (prev.logo || null)),
  };
  save();
  return o.branding;
}

/** Per-org summary counts for the platform console. */
export function platformOrgs() {
  const users = {}, comps = {};
  for (const u of Object.values(db.users)) users[u.orgId] = (users[u.orgId] || 0) + 1;
  for (const c of Object.values(db.computers)) comps[c.orgId] = (comps[c.orgId] || 0) + 1;
  return Object.values(db.orgs).map((o) => ({
    id: o.id, name: o.name, plan: o.plan || 'trial', createdAt: o.createdAt,
    users: users[o.id] || 0, computers: comps[o.id] || 0,
  }));
}

// Create a brand-new org with its first (admin/owner) user.
export function createOrgWithOwner({ orgName, email, name, passHash }) {
  const org = { id: id('org_'), name: orgName, plan: 'trial', createdAt: now() };
  db.orgs[org.id] = org;
  const user = {
    id: id('usr_'), orgId: org.id, email: email.toLowerCase(), name: name || email,
    passHash, role: 'owner', groupIds: [], createdAt: now(),
  };
  db.users[user.id] = user;
  save();
  return { org, user };
}

// Create a user inside an existing org (invite acceptance).
export function createUser({ orgId, email, name, passHash, role }) {
  const user = {
    id: id('usr_'), orgId, email: email.toLowerCase(), name: name || email,
    passHash, role: normRole(role), groupIds: [], createdAt: now(),
  };
  db.users[user.id] = user;
  save();
  return user;
}

export function listUsers(orgId) {
  return Object.values(db.users).filter((u) => u.orgId === orgId);
}

export function setUserGroups(userId, groupIds) {
  const u = db.users[userId];
  if (!u) return null;
  const valid = new Set(Object.values(db.groups).filter((g) => g.orgId === u.orgId).map((g) => g.id));
  u.groupIds = [...new Set(groupIds)].filter((g) => valid.has(g));
  save();
  return u;
}

export function setUserRole(userId, role) {
  const u = db.users[userId];
  if (!u || u.role === 'owner') return null;   // owner role is immutable
  u.role = normRole(role);
  save();
  return u;
}

export function deleteUser(userId) { delete db.users[userId]; save(); }

// ------------------------------- invites -------------------------------

export function createInvite({ orgId, email, role, invitedBy }) {
  const token = randomBytes(24).toString('hex');
  db.invites[token] = {
    token, orgId, email: (email || '').toLowerCase(), role: normRole(role),
    invitedBy, createdAt: now(), acceptedBy: null,
  };
  save();
  return db.invites[token];
}

export function getInvite(token) {
  const inv = db.invites[token];
  return inv && !inv.acceptedBy ? inv : null;
}

export function listInvites(orgId) {
  return Object.values(db.invites).filter((i) => i.orgId === orgId && !i.acceptedBy);
}

export function acceptInvite(token, userId) {
  const inv = db.invites[token];
  if (inv) { inv.acceptedBy = userId; save(); }
}

export function revokeInvite(token, orgId) {
  const inv = db.invites[token];
  if (inv && inv.orgId === orgId) { delete db.invites[token]; save(); }
}

// ------------------------------- groups -------------------------------

export function createGroup(orgId, name) {
  const g = { id: id('grp_'), orgId, name: name || 'Group', description: '', email: '', phone: '', address: '' };
  db.groups[g.id] = g;
  save();
  return g;
}

// Update a group's editable fields (name + description + contact info).
export function updateGroup(groupId, orgId, fields) {
  const g = db.groups[groupId];
  if (!g || g.orgId !== orgId) return null;
  if (typeof fields.name === 'string' && fields.name.trim()) g.name = fields.name.trim();
  for (const k of ['description', 'email', 'phone', 'address'])
    if (typeof fields[k] === 'string') g[k] = fields[k];
  save();
  return g;
}

// Set exactly which users and devices belong to a group (from the group editor).
export function setGroupMembers(groupId, orgId, { userIds, deviceTokens }) {
  const g = db.groups[groupId];
  if (!g || g.orgId !== orgId) return null;
  if (Array.isArray(userIds)) {
    const want = new Set(userIds);
    for (const u of Object.values(db.users)) {
      if (u.orgId !== orgId) continue;
      u.groupIds = u.groupIds || [];
      const has = u.groupIds.includes(groupId);
      if (want.has(u.id) && !has) u.groupIds.push(groupId);
      else if (!want.has(u.id) && has) u.groupIds = u.groupIds.filter((x) => x !== groupId);
    }
  }
  if (Array.isArray(deviceTokens)) {
    const want = new Set(deviceTokens);
    for (const c of Object.values(db.computers)) {
      if (c.orgId !== orgId) continue;
      if (want.has(c.deviceToken)) c.groupId = groupId;
      else if (c.groupId === groupId) c.groupId = null;
    }
  }
  save();
  return g;
}

export function listGroups(orgId) {
  return Object.values(db.groups).filter((g) => g.orgId === orgId);
}

export function getGroup(orgId, id) {
  const g = db.groups[id];
  return g && g.orgId === orgId ? { id: g.id, name: g.name } : null;
}

export function renameGroup(groupId, orgId, name) {
  const g = db.groups[groupId];
  if (!g || g.orgId !== orgId) return null;
  g.name = name || g.name;
  save();
  return g;
}

export function deleteGroup(groupId, orgId) {
  const g = db.groups[groupId];
  if (!g || g.orgId !== orgId) return;
  delete db.groups[groupId];
  for (const u of Object.values(db.users)) u.groupIds = (u.groupIds || []).filter((x) => x !== groupId);
  for (const c of Object.values(db.computers)) if (c.groupId === groupId) c.groupId = null;
  save();
}

// ------------------------------- enrollment tokens -------------------------------
// A pre-baked token an admin hands to a machine so it enrolls into the org without an
// interactive login (Phase 0). Reusable until revoked; optionally pins a target group.

export function createEnrollToken(orgId, { groupId, label, createdBy }) {
  const token = randomBytes(20).toString('hex');
  db.enrollTokens[token] = {
    token, orgId,
    groupId: groupId && db.groups[groupId]?.orgId === orgId ? groupId : null,
    label: (label || '').trim(), createdBy: createdBy || null, createdAt: now(),
    expiresAt: now() + ENROLL_TTL_MS,
  };
  save();
  return db.enrollTokens[token];
}

// Expired tokens read as absent, so a leaked one stops enrolling rogue devices on its own.
export function getEnrollToken(token) {
  const e = db.enrollTokens[token];
  if (!e) return null;
  return (e.expiresAt && e.expiresAt < now()) ? null : e;
}

export function listEnrollTokens(orgId) {
  return Object.values(db.enrollTokens).filter((e) => e.orgId === orgId);
}

export function revokeEnrollToken(token, orgId) {
  const e = db.enrollTokens[token];
  if (e && e.orgId === orgId) { delete db.enrollTokens[token]; save(); }
}

// ------------------------------- computers -------------------------------

// Upsert the computer identified by its device token when it comes online under
// an org (host signed in). Returns the record.
export function upsertComputer({ deviceToken, orgId, defaultName, relayId, groupId, mac }) {
  let c = db.computers[deviceToken];
  if (!c) {
    c = { orgId, name: defaultName || 'Computer', groupId: groupId || null, lastSeen: now(), relayId, mac: mac || null };
    db.computers[deviceToken] = c;
  } else {
    c.orgId = orgId;                 // (re)claim under this org
    c.relayId = relayId;
    c.lastSeen = now();
    if (!c.name) c.name = defaultName || 'Computer';
    if (mac) c.mac = mac;            // remember MAC for Wake-on-LAN
    // An enrollment token may pin a group; apply only if not already grouped.
    if (groupId && !c.groupId && db.groups[groupId]?.orgId === orgId) c.groupId = groupId;
  }
  save();
  return c;
}

export function touchComputer(deviceToken) {
  const c = db.computers[deviceToken];
  if (c) { c.lastSeen = now(); save(); }
}

// Live health metrics from a host. Kept in memory (not persisted — ephemeral), with
// edge-triggered alerts logged to the audit trail when a metric crosses its threshold.
const ALERT_THRESHOLD = { cpu: 95, mem: 95, disk: 95 };
export function updateMetrics(deviceToken, m) {
  const c = db.computers[deviceToken];
  if (!c) return;
  c.metrics = { cpu: m.cpu | 0, mem: m.mem | 0, disk: m.disk | 0, ts: now() };
  c._alerting = c._alerting || {};
  for (const k of ['cpu', 'mem', 'disk']) {
    const over = c.metrics[k] >= ALERT_THRESHOLD[k];
    if (over && !c._alerting[k]) {
      c._alerting[k] = true;
      logEvent(c.orgId, 'alert', { target: c.name, detail: `${k.toUpperCase()} ${c.metrics[k]}%` }); // logEvent saves
    } else if (!over) {
      c._alerting[k] = false;
    }
  }
}

export function listComputers(orgId) {
  return Object.entries(db.computers)
    .filter(([, c]) => c.orgId === orgId)
    .map(([deviceToken, c]) => ({ deviceToken, ...c }));
}

export function findComputerByToken(deviceToken) {
  const c = db.computers[deviceToken];
  return c ? { deviceToken, ...c } : null;
}

export function setComputerGroup(deviceToken, orgId, groupId) {
  const c = db.computers[deviceToken];
  if (!c || c.orgId !== orgId) return null;
  c.groupId = groupId && db.groups[groupId] && db.groups[groupId].orgId === orgId ? groupId : null;
  save();
  return c;
}

export function renameComputer(deviceToken, orgId, name) {
  const c = db.computers[deviceToken];
  if (!c || c.orgId !== orgId) return null;
  c.name = name || c.name;
  save();
  return c;
}

// Can `user` connect to the computer identified by its device token? (permission matrix)
export function userCanAccessComputer(user, deviceToken) {
  const c = db.computers[deviceToken];
  if (!c || c.orgId !== user.orgId) return false;
  if (isManager(user)) return true;             // owner/admin → all devices
  if (user.role === 'viewer') return false;     // auditor → read-only, no control
  return !!c.groupId && (user.groupIds || []).includes(c.groupId);  // technician/operator → their groups
}
