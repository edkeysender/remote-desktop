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

// Create a brand-new org with its first (admin/owner) user.
export function createOrgWithOwner({ orgName, email, name, passHash }) {
  const org = { id: id('org_'), name: orgName, createdAt: now() };
  db.orgs[org.id] = org;
  const user = {
    id: id('usr_'), orgId: org.id, email: email.toLowerCase(), name: name || email,
    passHash, role: 'admin', groupIds: [], createdAt: now(),
  };
  db.users[user.id] = user;
  save();
  return { org, user };
}

// Create a user inside an existing org (invite acceptance).
export function createUser({ orgId, email, name, passHash, role }) {
  const user = {
    id: id('usr_'), orgId, email: email.toLowerCase(), name: name || email,
    passHash, role: role === 'admin' ? 'admin' : 'user', groupIds: [], createdAt: now(),
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
  if (!u) return null;
  u.role = role === 'admin' ? 'admin' : 'user';
  save();
  return u;
}

export function deleteUser(userId) { delete db.users[userId]; save(); }

// ------------------------------- invites -------------------------------

export function createInvite({ orgId, email, role, invitedBy }) {
  const token = randomBytes(24).toString('hex');
  db.invites[token] = {
    token, orgId, email: (email || '').toLowerCase(), role: role === 'admin' ? 'admin' : 'user',
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
export function upsertComputer({ deviceToken, orgId, defaultName, relayId, groupId }) {
  let c = db.computers[deviceToken];
  if (!c) {
    c = { orgId, name: defaultName || 'Computer', groupId: groupId || null, lastSeen: now(), relayId };
    db.computers[deviceToken] = c;
  } else {
    c.orgId = orgId;                 // (re)claim under this org
    c.relayId = relayId;
    c.lastSeen = now();
    if (!c.name) c.name = defaultName || 'Computer';
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

// Can `user` connect to the computer identified by its device token?
export function userCanAccessComputer(user, deviceToken) {
  const c = db.computers[deviceToken];
  if (!c || c.orgId !== user.orgId) return false;
  if (user.role === 'admin') return true;
  return !!c.groupId && (user.groupIds || []).includes(c.groupId);
}
