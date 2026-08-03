// The authenticated control-plane web app (Express): registration, login, invites,
// groups, users, and computers — all scoped to the caller's org. Mounted by index.js
// onto the shared HTTP server. `deps.relayStatus(relayId)` reports live online/busy.

import express from 'express';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';
import * as store from './store.js';
import {
  hashPassword, verifyPassword, issueToken, verifyToken, parseCookies, SESSION_COOKIE,
} from './auth.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PUBLIC_DIR = join(__dirname, '..', 'public');

export function buildWebApp({ relayStatus, sendCommand, onlinePeer }) {
  const app = express.Router();
  // 8 MB: branding logos, device wallpapers and login backgrounds are data-URL JSON bodies
  // that exceed Express's 100 KB default (was causing PayloadTooLargeError on upload).
  app.use(express.json({ limit: '8mb' }));

  // Attach req.user from the session cookie or a Bearer token (desktop app).
  app.use((req, _res, next) => {
    let token = parseCookies(req)[SESSION_COOKIE];
    const auth = req.headers['authorization'];
    if (!token && auth?.startsWith('Bearer ')) token = auth.slice(7);
    const userId = token ? verifyToken(token) : null;
    req.user = userId ? store.getUser(userId) : null;
    // Fall back to an org API token (MCP / integrations) → a manager-scoped principal.
    if (!req.user && token) {
      const orgId = store.resolveApiToken(token);
      if (orgId) { req.user = { id: 'api:' + orgId, orgId, role: 'admin', email: 'api-token', name: 'API token', groupIds: [] }; req.apiPrincipal = true; }
    }
    next();
  });

  const requireUser = (req, res, next) => req.user ? next() : res.status(401).json({ error: 'not signed in' });
  const requireHuman = (req, res, next) => req.apiPrincipal ? res.status(403).json({ error: 'not allowed for API tokens' }) : next();
  const requireAdmin = (req, res, next) =>
    store.isManager(req.user) ? next() : res.status(403).json({ error: 'admin only' });
  const requireAuditor = (req, res, next) =>
    store.canAudit(req.user) ? next() : res.status(403).json({ error: 'not permitted' });

  const setSession = (res, userId) => {
    const token = issueToken(userId);
    res.cookie?.(SESSION_COOKIE, token, { httpOnly: true, sameSite: 'lax', maxAge: 30 * 864e5 });
    // Router has no res.cookie without cookie middleware; set header directly.
    res.setHeader('Set-Cookie',
      `${SESSION_COOKIE}=${token}; HttpOnly; SameSite=Lax; Path=/; Max-Age=${30 * 86400}`);
    return token;
  };

  const publicUser = (u) => ({ id: u.id, email: u.email, name: u.name, role: u.role, groupIds: u.groupIds || [] });

  // ------------------------------- auth -------------------------------

  app.post('/api/register', async (req, res) => {
    const { orgName, email, name, password } = req.body || {};
    if (!orgName || !email || !password) return res.status(400).json({ error: 'orgName, email, password required' });
    if (String(password).length < 8) return res.status(400).json({ error: 'password must be at least 8 characters' });
    if (store.emailTaken(email)) return res.status(409).json({ error: 'that email is already registered' });
    const { org, user } = store.createOrgWithOwner({ orgName, email, name, passHash: await hashPassword(password) });
    store.logEvent(org.id, 'org.create', { actorEmail: user.email, target: org.name });
    const token = setSession(res, user.id);
    res.json({ token, user: publicUser(user), org: { id: org.id, name: org.name } });
  });

  app.post('/api/login', async (req, res) => {
    const { email, password } = req.body || {};
    const user = store.findUserByEmail(email || '');
    if (!user || !(await verifyPassword(password || '', user.passHash)))
      return res.status(401).json({ error: 'wrong email or password' });
    const token = setSession(res, user.id);
    const org = store.getOrg(user.orgId);
    store.logEvent(user.orgId, 'login', { actorEmail: user.email });
    res.json({ token, user: publicUser(user), org: { id: org.id, name: org.name } });
  });

  app.post('/api/logout', (req, res) => {
    res.setHeader('Set-Cookie', `${SESSION_COOKIE}=; HttpOnly; Path=/; Max-Age=0`);
    res.json({ ok: true });
  });

  app.get('/api/me', requireUser, (req, res) => {
    const org = store.getOrg(req.user.orgId);
    res.json({ user: publicUser(req.user), org: { id: org.id, name: org.name }, branding: store.getBranding(req.user.orgId) });
  });

  // ---- org API tokens (for the MCP server); managed by humans only ----
  app.get('/api/api-tokens', requireUser, requireAdmin, requireHuman, (req, res) => res.json(store.listApiTokens(req.user.orgId)));
  app.post('/api/api-tokens', requireUser, requireAdmin, requireHuman, (req, res) => {
    const t = store.createApiToken(req.user.orgId, req.body?.label);
    store.logEvent(req.user.orgId, 'api.token', { actorEmail: req.user.email, target: t?.label });
    res.json(t);
  });
  app.delete('/api/api-tokens/:token', requireUser, requireAdmin, requireHuman, (req, res) => {
    store.revokeApiToken(req.user.orgId, req.params.token);
    res.json({ ok: true });
  });

  // ---- per-org network / ICE (STUN/TURN): any member reads; managers set ----
  app.get('/api/ice', requireUser, (req, res) => res.json(store.getIce(req.user.orgId)));
  app.put('/api/ice', requireUser, requireAdmin, (req, res) => {
    const i = store.setIce(req.user.orgId, req.body || {});
    store.logEvent(req.user.orgId, 'network', { actorEmail: req.user.email });
    res.json(i);
  });

  // ---- per-org branding (white-label): any member can read; managers can set ----
  app.get('/api/branding', requireUser, (req, res) => res.json(store.getBranding(req.user.orgId)));
  app.put('/api/branding', requireUser, requireAdmin, (req, res) => {
    const b = store.setBranding(req.user.orgId, req.body || {});
    store.logEvent(req.user.orgId, 'branding', { actorEmail: req.user.email, target: b?.appName });
    res.json(b);
  });

  // ------------------------------- invites -------------------------------

  app.get('/api/invites', requireUser, requireAdmin, (req, res) => {
    res.json(store.listInvites(req.user.orgId).map((i) => ({
      token: i.token, email: i.email, role: i.role, createdAt: i.createdAt,
      url: inviteUrl(req, i.token),
    })));
  });

  app.post('/api/invites', requireUser, requireAdmin, (req, res) => {
    const { email, role } = req.body || {};
    const inv = store.createInvite({ orgId: req.user.orgId, email, role, invitedBy: req.user.id });
    store.logEvent(req.user.orgId, 'user.invite', { actorEmail: req.user.email, target: inv.email, detail: `role ${inv.role}` });
    res.json({ token: inv.token, email: inv.email, role: inv.role, url: inviteUrl(req, inv.token) });
  });

  app.delete('/api/invites/:token', requireUser, requireAdmin, (req, res) => {
    store.revokeInvite(req.params.token, req.user.orgId);
    res.json({ ok: true });
  });

  // Public: look up an invite so the accept page can show who/where.
  app.get('/api/invite/:token', (req, res) => {
    const inv = store.getInvite(req.params.token);
    if (!inv) return res.status(404).json({ error: 'invite not found or already used' });
    const org = store.getOrg(inv.orgId);
    res.json({ email: inv.email, role: inv.role, orgName: org?.name || '' });
  });

  app.post('/api/accept-invite', async (req, res) => {
    const { token, name, password } = req.body || {};
    const inv = store.getInvite(token);
    if (!inv) return res.status(404).json({ error: 'invite not found or already used' });
    const email = inv.email;
    if (!email) return res.status(400).json({ error: 'invite has no email' });
    if (String(password || '').length < 8) return res.status(400).json({ error: 'password must be at least 8 characters' });
    if (store.emailTaken(email)) return res.status(409).json({ error: 'that email is already registered' });
    const user = store.createUser({ orgId: inv.orgId, email, name, passHash: await hashPassword(password), role: inv.role });
    store.acceptInvite(token, user.id);
    store.logEvent(inv.orgId, 'user.join', { actorEmail: user.email, detail: `role ${user.role}` });
    const t = setSession(res, user.id);
    const org = store.getOrg(user.orgId);
    res.json({ token: t, user: publicUser(user), org: { id: org.id, name: org.name } });
  });

  // ------------------------------- groups -------------------------------

  app.get('/api/groups', requireUser, (req, res) => {
    const users = store.listUsers(req.user.orgId);
    const comps = store.listComputers(req.user.orgId);
    res.json(store.listGroups(req.user.orgId).map((g) => ({
      id: g.id, name: g.name, description: g.description || '',
      email: g.email || '', phone: g.phone || '', address: g.address || '',
      deviceCount: comps.filter((c) => c.groupId === g.id).length,
      userCount: users.filter((u) => (u.groupIds || []).includes(g.id)).length,
    })));
  });
  app.post('/api/groups', requireUser, requireAdmin, (req, res) => {
    const g = store.createGroup(req.user.orgId, (req.body?.name || '').trim());
    store.logEvent(req.user.orgId, 'group.create', { actorEmail: req.user.email, target: g.name });
    res.json(g);
  });
  app.patch('/api/groups/:id', requireUser, requireAdmin, (req, res) => {
    const g = store.updateGroup(req.params.id, req.user.orgId, req.body || {});
    if (!g) return res.status(404).json({ error: 'no such group' });
    store.logEvent(req.user.orgId, 'group.update', { actorEmail: req.user.email, target: g.name });
    res.json(g);
  });
  // Set which users + devices belong to a group (from the group editor).
  app.put('/api/groups/:id/members', requireUser, requireAdmin, (req, res) => {
    const g = store.setGroupMembers(req.params.id, req.user.orgId, {
      userIds: req.body?.userIds, deviceTokens: req.body?.deviceTokens,
    });
    if (!g) return res.status(404).json({ error: 'no such group' });
    store.logEvent(req.user.orgId, 'group.members', { actorEmail: req.user.email, target: g.name });
    res.json({ ok: true });
  });
  app.delete('/api/groups/:id', requireUser, requireAdmin, (req, res) => {
    store.deleteGroup(req.params.id, req.user.orgId);
    res.json({ ok: true });
  });

  // ------------------------------- users -------------------------------

  app.get('/api/users', requireUser, requireAdmin, (req, res) =>
    res.json(store.listUsers(req.user.orgId).map(publicUser)));
  app.patch('/api/users/:id', requireUser, requireAdmin, (req, res) => {
    const target = store.getUser(req.params.id);
    if (!target || target.orgId !== req.user.orgId) return res.status(404).json({ error: 'no such user' });
    if (Array.isArray(req.body?.groupIds)) {
      store.setUserGroups(target.id, req.body.groupIds);
      store.logEvent(req.user.orgId, 'user.groups', { actorEmail: req.user.email, target: target.email });
    }
    if (req.body?.role && target.id !== req.user.id) {
      store.setUserRole(target.id, req.body.role); // can't demote self
      store.logEvent(req.user.orgId, 'user.role', { actorEmail: req.user.email, target: target.email, detail: req.body.role });
    }
    res.json(publicUser(store.getUser(target.id)));
  });
  app.delete('/api/users/:id', requireUser, requireAdmin, (req, res) => {
    const target = store.getUser(req.params.id);
    if (!target || target.orgId !== req.user.orgId || target.id === req.user.id)
      return res.status(400).json({ error: 'cannot delete' });
    store.deleteUser(target.id);
    res.json({ ok: true });
  });

  // ------------------------------- computers -------------------------------

  const decorate = (c) => {
    const st = relayStatus(c.relayId) || { online: false, busy: false };
    return {
      deviceToken: c.deviceToken, name: c.name, groupId: c.groupId,
      relayId: st.online ? c.relayId : null, online: st.online, busy: st.busy, lastSeen: c.lastSeen,
      metrics: st.online ? (c.metrics || null) : null, wakeable: !!c.mac, configId: c.configId || null,
    };
  };

  // Managers + auditors: read every computer in the org (auditors read-only).
  app.get('/api/computers', requireUser, requireAuditor, (req, res) =>
    res.json(store.listComputers(req.user.orgId).map(decorate)));

  // Host administration panel: one computer + its recent sessions and alerts.
  app.get('/api/computers/:deviceToken', requireUser, requireAuditor, (req, res) => {
    const dt = req.params.deviceToken;
    const c = store.findComputerByToken(dt);
    if (!c || c.orgId !== req.user.orgId) return res.status(404).json({ error: 'no such computer' });
    const sessions = store.listSessions(req.user.orgId, 1000).filter((s) => s.deviceToken === dt).slice(0, 25);
    const events = store.listEvents(req.user.orgId, 1000).filter((e) => e.target === c.name).slice(0, 25);
    res.json({ computer: decorate(c), sessions, events });
  });

  // Task manager over the command channel (online devices only).
  const liveComputer = (req, res) => {
    const c = store.findComputerByToken(req.params.deviceToken);
    if (!c || c.orgId !== req.user.orgId) { res.status(404).json({ error: 'no such computer' }); return null; }
    if (!relayStatus(c.relayId).online) { res.status(409).json({ error: 'device offline' }); return null; }
    return c;
  };

  app.get('/api/computers/:deviceToken/tasks', requireUser, requireAdmin, async (req, res) => {
    const c = liveComputer(req, res); if (!c) return;
    try { const r = await sendCommand(c.relayId, 'tasklist'); res.json({ tasks: r.tasks || [] }); }
    catch (e) { res.status(504).json({ error: e.message }); }
  });

  app.post('/api/computers/:deviceToken/kill', requireUser, requireAdmin, async (req, res) => {
    const c = liveComputer(req, res); if (!c) return;
    const pid = Number(req.body?.pid);
    if (!Number.isInteger(pid)) return res.status(400).json({ error: 'pid required' });
    try {
      const r = await sendCommand(c.relayId, 'kill', { pid });
      if (!r.ok) return res.status(400).json({ error: r.error || 'kill failed' });
      store.logEvent(req.user.orgId, 'task.kill', { actorEmail: req.user.email, target: c.name, detail: `pid ${pid}` });
      res.json({ ok: true });
    } catch (e) { res.status(504).json({ error: e.message }); }
  });

  // ---- device configurations (managers) ----
  app.get('/api/configs', requireUser, requireAdmin, (req, res) => res.json(store.listConfigs(req.user.orgId)));
  app.post('/api/configs', requireUser, requireAdmin, (req, res) => {
    const c = store.createConfig(req.user.orgId, req.body?.name);
    store.logEvent(req.user.orgId, 'config.create', { actorEmail: req.user.email, target: c?.name });
    res.json(c);
  });
  app.put('/api/configs/:id', requireUser, requireAdmin, (req, res) => {
    const c = store.updateConfig(req.user.orgId, req.params.id, req.body || {});
    c ? res.json(c) : res.status(404).json({ error: 'no such configuration' });
  });
  app.delete('/api/configs/:id', requireUser, requireAdmin, (req, res) => {
    store.deleteConfig(req.user.orgId, req.params.id);
    res.json({ ok: true });
  });

  // Apply the device's assigned configuration on the host and report per-check results.
  app.post('/api/computers/:deviceToken/apply-config', requireUser, requireAdmin, async (req, res) => {
    const c = liveComputer(req, res); if (!c) return;
    const cfg = c.configId ? store.getConfig(req.user.orgId, c.configId) : null;
    if (!cfg) return res.status(400).json({ error: 'no configuration assigned to this device' });
    try {
      const r = await sendCommand(c.relayId, 'config', cfg, 90000);
      store.logEvent(req.user.orgId, 'config.apply', { actorEmail: req.user.email, target: c.name, detail: cfg.name });
      res.json({ ok: !!r.ok, results: r.results || [] });
    } catch (e) { res.status(504).json({ error: e.message }); }
  });

  // Wake-on-LAN: an online peer in the org broadcasts the magic packet to the target MAC.
  app.post('/api/computers/:deviceToken/wake', requireUser, requireAdmin, async (req, res) => {
    const c = store.findComputerByToken(req.params.deviceToken);
    if (!c || c.orgId !== req.user.orgId) return res.status(404).json({ error: 'no such computer' });
    if (!c.mac) return res.status(400).json({ error: 'no MAC on record — the device must connect once on a WOL-capable version' });
    const peer = onlinePeer(req.user.orgId, req.params.deviceToken);
    if (!peer) return res.status(409).json({ error: 'need another online device in this organization to send the wake packet' });
    try {
      const r = await sendCommand(peer, 'wol', { mac: c.mac });
      if (!r.ok) return res.status(502).json({ error: r.error || 'wake failed' });
      store.logEvent(req.user.orgId, 'wake', { actorEmail: req.user.email, target: c.name });
      res.json({ ok: true });
    } catch (e) { res.status(504).json({ error: e.message }); }
  });

  app.patch('/api/computers/:deviceToken', requireUser, requireAdmin, (req, res) => {
    const dt = req.params.deviceToken;
    if (typeof req.body?.name === 'string') store.renameComputer(dt, req.user.orgId, req.body.name.trim());
    if ('groupId' in (req.body || {})) store.setComputerGroup(dt, req.user.orgId, req.body.groupId || null);
    if ('configId' in (req.body || {})) store.setComputerConfig(dt, req.user.orgId, req.body.configId || null);
    const c = store.findComputerByToken(dt);
    c && c.orgId === req.user.orgId ? res.json(decorate(c)) : res.status(404).json({ error: 'no such computer' });
  });

  // ------------------------------- enrollment tokens (admin) -------------------------------

  app.get('/api/enroll-tokens', requireUser, requireAdmin, (req, res) => {
    const gname = Object.fromEntries(store.listGroups(req.user.orgId).map((g) => [g.id, g.name]));
    res.json(store.listEnrollTokens(req.user.orgId).map((e) => ({
      token: e.token, label: e.label, groupId: e.groupId,
      groupName: e.groupId ? gname[e.groupId] || null : null, createdAt: e.createdAt,
    })));
  });

  app.post('/api/enroll-tokens', requireUser, requireAdmin, (req, res) => {
    const e = store.createEnrollToken(req.user.orgId, {
      groupId: req.body?.groupId || null, label: req.body?.label || '', createdBy: req.user.id,
    });
    store.logEvent(req.user.orgId, 'enroll.token', { actorEmail: req.user.email, target: e.label || '(token)' });
    res.json({ token: e.token, label: e.label, groupId: e.groupId });
  });

  app.delete('/api/enroll-tokens/:token', requireUser, requireAdmin, (req, res) => {
    store.revokeEnrollToken(req.params.token, req.user.orgId);
    res.json({ ok: true });
  });

  // ------------------------------- audit + sessions (admin) -------------------------------

  app.get('/api/audit', requireUser, requireAuditor, (req, res) =>
    res.json(store.listEvents(req.user.orgId, 300)));

  app.get('/api/sessions', requireUser, requireAuditor, (req, res) =>
    res.json(store.listSessions(req.user.orgId, 200)));

  // Any user: the groups + computers they're allowed to connect to (desktop picker).
  app.get('/api/my-computers', requireUser, (req, res) => {
    const groups = store.listGroups(req.user.orgId);
    const computers = store.listComputers(req.user.orgId)
      .filter((c) => store.userCanAccessComputer(req.user, c.deviceToken))
      .map(decorate);
    res.json({ groups, computers, admin: req.user.role === 'admin' });
  });

  // ------------------------------- static SPA -------------------------------

  app.use(express.static(PUBLIC_DIR));
  // Client-side routes fall back to the SPA shell.
  app.get(['/', '/login', '/invite/:token', '/app', '/app/*'], (_req, res) =>
    res.sendFile(join(PUBLIC_DIR, 'index.html')));

  return app;
}

function inviteUrl(req, token) {
  const proto = req.headers['x-forwarded-proto'] || req.protocol || 'http';
  const host = req.headers['host'];
  return `${proto}://${host}/invite/${token}`;
}
