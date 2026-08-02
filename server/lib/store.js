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

import { randomBytes } from 'crypto';
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs';
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
function save() {
  try { mkdirSync(DATA_DIR, { recursive: true }); writeFileSync(DB_FILE, JSON.stringify(db, null, 2)); }
  catch (e) { console.error('[store] save failed:', e.message); }
}
load();

const id = (p) => p + randomBytes(6).toString('hex');
const now = () => Date.now();

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

// ---- org API tokens (for the MCP server / integrations; manager-scoped) ----
export function createApiToken(orgId, label) {
  const o = db.orgs[orgId]; if (!o) return null;
  o.apiTokens = o.apiTokens || {};
  const token = 'hk_' + randomBytes(24).toString('hex');
  o.apiTokens[token] = { label: (label || '').toString().slice(0, 60), createdAt: now() };
  save();
  return { token, label: o.apiTokens[token].label };
}
export function listApiTokens(orgId) {
  const o = db.orgs[orgId];
  return o?.apiTokens ? Object.entries(o.apiTokens).map(([token, v]) => ({ token, label: v.label, createdAt: v.createdAt })) : [];
}
export function revokeApiToken(orgId, token) {
  const o = db.orgs[orgId];
  if (o?.apiTokens && o.apiTokens[token]) { delete o.apiTokens[token]; save(); }
}
export function resolveApiToken(token) {
  for (const [orgId, o] of Object.entries(db.orgs)) if (o.apiTokens && o.apiTokens[token]) return orgId;
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

// ---- per-org branding (white-label) ----
export function getBranding(orgId) {
  const b = db.orgs[orgId]?.branding || {};
  return { appName: b.appName || 'Hangar', accent: b.accent || '#5B5BF5', logo: b.logo || null };
}
export function setBranding(orgId, { appName, accent, logo } = {}) {
  const o = db.orgs[orgId];
  if (!o) return null;
  const prev = o.branding || {};
  o.branding = {
    appName: (appName || '').toString().trim().slice(0, 40) || 'Hangar',
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
  const g = { id: id('grp_'), orgId, name: name || 'Group' };
  db.groups[g.id] = g;
  save();
  return g;
}

export function listGroups(orgId) {
  return Object.values(db.groups).filter((g) => g.orgId === orgId);
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
  };
  save();
  return db.enrollTokens[token];
}

export function getEnrollToken(token) { return db.enrollTokens[token] || null; }

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
