// Signaling server + Phase-0 relay.
//
// Phase 0: this process also pipes JPEG frames (host->viewer) and input JSON
// (viewer->host). From Phase 2 the media goes P2P over WebRTC and this server
// only relays SDP/ICE, then gets out of the way.
//
// It never verifies the password — the host does that. It only routes by ID.

import { WebSocketServer } from 'ws';
import { createServer } from 'http';
import { randomBytes, timingSafeEqual } from 'crypto';
import { readFileSync, writeFileSync, existsSync, createReadStream, statSync } from 'fs';
import { resolve, join, basename, extname, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));

const PORT = process.env.PORT ? Number(process.env.PORT) : 8080;
const ID_MAP_FILE = process.env.ID_MAP_FILE || 'idmap.json';
// Directory of app-update artifacts (manifest.json + the published .exe files).
// The Windows apps poll <http>/update/manifest.json and download from here.
const UPDATE_DIR = resolve(process.env.UPDATE_DIR || './update');
// Admin panel: persisted groups + client metadata, and the panel password.
const ADMIN_FILE = process.env.ADMIN_FILE || 'admin.json';
const ADMIN_PASSWORD = process.env.ADMIN_PASSWORD || 'admin';

// Persistent host identity: a host may present a stable `token` (stored on its
// machine) to be assigned the SAME ID every time — this is what makes unattended
// access usable (the ID printed on the machine doesn't change across restarts).
// File-backed so IDs survive a server restart. For scale, back this with a DB.
/** @type {Map<string,string>} token -> id */
const tokenToId = new Map();
function loadIdMap() {
  try {
    if (existsSync(ID_MAP_FILE))
      for (const [tok, id] of Object.entries(JSON.parse(readFileSync(ID_MAP_FILE, 'utf8'))))
        tokenToId.set(tok, id);
  } catch (e) { console.error('[server] could not load id map:', e.message); }
}
function saveIdMap() {
  try { writeFileSync(ID_MAP_FILE, JSON.stringify(Object.fromEntries(tokenToId), null, 2)); }
  catch (e) { console.error('[server] could not save id map:', e.message); }
}
loadIdMap();

/** @type {Map<string, {ws: import('ws').WebSocket, viewer: import('ws').WebSocket|null, ip: string, since: number}>} */
const hosts = new Map();        // id -> host entry
const pending = new Map();      // requestId -> viewer ws (awaiting host's password check)

// Admin state: named groups and per-client metadata (friendly name, group, last-seen),
// persisted so it survives restarts and covers offline clients too.
// { groups: [{id,name}], clients: { <id>: {name, groupId, lastSeen} } }
let admin = { groups: [], clients: {} };
function loadAdmin() {
  try {
    if (existsSync(ADMIN_FILE)) {
      const a = JSON.parse(readFileSync(ADMIN_FILE, 'utf8'));
      admin = { groups: Array.isArray(a.groups) ? a.groups : [], clients: a.clients || {} };
    }
  } catch (e) { console.error('[server] could not load admin store:', e.message); }
}
function saveAdmin() {
  try { writeFileSync(ADMIN_FILE, JSON.stringify(admin, null, 2)); }
  catch (e) { console.error('[server] could not save admin store:', e.message); }
}
loadAdmin();

// Ensure a metadata record exists for an id (so it persists after going offline).
function ensureClientMeta(id) {
  if (!admin.clients[id]) admin.clients[id] = { name: '', groupId: null, lastSeen: 0 };
  return admin.clients[id];
}

// HTTP server shares the port with the WebSocket. It serves:
//   GET  /health                    → liveness check
//   GET  /update/manifest.json      → the current version manifest
//   GET  /update/<file>             → a published app artifact (by bare filename)
//   GET  /directory                 → groups + clients for the Master picker (auth)
//   *    /admin, /admin/api/*        → admin panel + API (auth)
// Everything else 404s. The WebSocket upgrade is handled by the wss below.
const ALLOWED_EXT = new Set(['.json', '.exe', '.zip']);
const http = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');

  // ---- admin panel + API and the master directory (all password-protected) ----
  if (url.pathname === '/admin' || url.pathname.startsWith('/admin/') || url.pathname === '/directory') {
    if (!checkAuth(req)) {
      res.writeHead(401, { 'WWW-Authenticate': 'Basic realm="FTD Admin"' }).end('auth required');
      return;
    }
    try { await handleAdmin(req, res, url); }
    catch (e) { sendJson(res, { error: e.message }, 500); }
    return;
  }

  // ---- public GET endpoints ----
  if (req.method !== 'GET') { res.writeHead(405).end(); return; }
  if (url.pathname === '/health') { res.writeHead(200).end('ok'); return; }

  if (url.pathname === '/update/manifest.json') return serveFile(res, join(UPDATE_DIR, 'manifest.json'), 'application/json');
  if (url.pathname.startsWith('/update/')) {
    // basename() strips any path components, so "../" can't escape UPDATE_DIR.
    const name = basename(decodeURIComponent(url.pathname.slice('/update/'.length)));
    if (!name || !ALLOWED_EXT.has(extname(name).toLowerCase())) { res.writeHead(404).end(); return; }
    const type = extname(name) === '.json' ? 'application/json' : 'application/octet-stream';
    return serveFile(res, join(UPDATE_DIR, name), type);
  }
  res.writeHead(404).end();
});

function serveFile(res, path, type) {
  let st;
  try { st = statSync(path); } catch { res.writeHead(404).end('not found'); return; }
  if (!st.isFile()) { res.writeHead(404).end('not found'); return; }
  res.writeHead(200, {
    'Content-Type': type,
    'Content-Length': st.size,
    'Cache-Control': 'no-cache',
  });
  createReadStream(path).pipe(res);
}

// ------------------------------- admin / directory -------------------------------

function clientIp(req) {
  const ip = req.socket.remoteAddress || '';
  return ip.startsWith('::ffff:') ? ip.slice(7) : ip;   // unwrap IPv4-mapped IPv6
}

function isAdminPassword(pw) {
  if (typeof pw !== 'string') return false;
  const a = Buffer.from(pw), b = Buffer.from(ADMIN_PASSWORD);
  return a.length === b.length && timingSafeEqual(a, b);
}

function checkAuth(req) {
  const m = /^Basic (.+)$/.exec(req.headers['authorization'] || '');
  if (!m) return false;
  const pass = Buffer.from(m[1], 'base64').toString().split(':').slice(1).join(':');
  return isAdminPassword(pass);
}

function sendJson(res, obj, code = 200) {
  res.writeHead(code, { 'Content-Type': 'application/json', 'Cache-Control': 'no-cache' });
  res.end(JSON.stringify(obj));
}

function readBody(req) {
  return new Promise((resolvePromise) => {
    let data = '';
    req.on('data', (c) => { data += c; if (data.length > 1e6) req.destroy(); });
    req.on('end', () => { try { resolvePromise(JSON.parse(data || '{}')); } catch { resolvePromise({}); } });
    req.on('error', () => resolvePromise({}));
  });
}

// A snapshot of every known client (online now, or seen before) plus the groups.
function adminState() {
  const persistentIds = new Set(tokenToId.values());
  const ids = new Set([...persistentIds, ...hosts.keys(), ...Object.keys(admin.clients)]);
  const clients = [...ids].map((id) => {
    const entry = hosts.get(id);
    const meta = admin.clients[id] || {};
    return {
      id,
      online: !!entry,
      busy: !!(entry && entry.viewer),
      ip: entry ? entry.ip : null,
      since: entry ? entry.since : null,
      name: meta.name || '',
      groupId: meta.groupId || null,
      lastSeen: meta.lastSeen || null,
      persistent: persistentIds.has(id),
    };
  });
  clients.sort((a, b) => (Number(b.online) - Number(a.online)) || a.id.localeCompare(b.id));
  return { groups: admin.groups, clients, now: Date.now() };
}

async function handleAdmin(req, res, url) {
  // The Master's read-only picker: groups + connectable clients.
  if (url.pathname === '/directory' && req.method === 'GET') {
    const s = adminState();
    return sendJson(res, {
      groups: s.groups,
      clients: s.clients.map((c) => ({ id: c.id, name: c.name, groupId: c.groupId, online: c.online, busy: c.busy })),
    });
  }

  if (url.pathname === '/admin' || url.pathname === '/admin/')
    return serveFile(res, join(__dirname, 'admin.html'), 'text/html; charset=utf-8');

  if (url.pathname === '/admin/api/state' && req.method === 'GET')
    return sendJson(res, adminState());

  if (url.pathname === '/admin/api/groups' && req.method === 'POST') {
    const body = await readBody(req);
    const name = (body.name || '').toString().trim() || 'Group';
    const group = { id: 'g' + randomBytes(4).toString('hex'), name };
    admin.groups.push(group);
    saveAdmin();
    return sendJson(res, group);
  }

  const gm = /^\/admin\/api\/groups\/([^/]+)$/.exec(url.pathname);
  if (gm) {
    const gid = decodeURIComponent(gm[1]);
    if (req.method === 'DELETE') {
      admin.groups = admin.groups.filter((g) => g.id !== gid);
      for (const c of Object.values(admin.clients)) if (c.groupId === gid) c.groupId = null;
      saveAdmin();
      return sendJson(res, { ok: true });
    }
    if (req.method === 'PATCH') {
      const body = await readBody(req);
      const g = admin.groups.find((x) => x.id === gid);
      if (!g) return sendJson(res, { error: 'no such group' }, 404);
      g.name = (body.name || '').toString().trim() || g.name;
      saveAdmin();
      return sendJson(res, g);
    }
  }

  const cm = /^\/admin\/api\/clients\/([^/]+)$/.exec(url.pathname);
  if (cm && req.method === 'PATCH') {
    const cid = decodeURIComponent(cm[1]);
    const body = await readBody(req);
    const meta = ensureClientMeta(cid);
    if (typeof body.name === 'string') meta.name = body.name.trim();
    if ('groupId' in body) {
      const gid = body.groupId;
      meta.groupId = gid && admin.groups.some((g) => g.id === gid) ? gid : null;
    }
    saveAdmin();
    return sendJson(res, { ok: true });
  }

  res.writeHead(404).end();
}

const wss = new WebSocketServer({ server: http });
http.listen(PORT, () => {
  console.log(`[server] signaling/relay listening on ws://0.0.0.0:${PORT}`);
  console.log(`[server] update artifacts served from ${UPDATE_DIR}`);
  console.log(`[server] admin panel at http://0.0.0.0:${PORT}/admin`);
  if (ADMIN_PASSWORD === 'admin')
    console.warn('[server] WARNING: admin password is the default "admin" — set ADMIN_PASSWORD to secure it.');
});

// Safety net: an unexpected error on one connection must never crash the relay and
// drop everyone's sessions. Log and keep serving.
process.on('uncaughtException', (e) => console.error('[server] uncaught:', e?.stack || e));

function newId() {
  // 9-digit numeric ID, avoids leading zero so it's always 9 chars.
  let id;
  do {
    id = String(100_000_000 + (randomBytes(4).readUInt32BE(0) % 900_000_000));
  } while (hosts.has(id));
  return id;
}

function send(ws, obj) {
  if (ws && ws.readyState === ws.OPEN) ws.send(JSON.stringify(obj));
}

wss.on('connection', (ws, req) => {
  ws.role = null;      // 'host' | 'viewer'
  ws.id = null;        // host id (both peers store the paired host id)
  ws._ip = clientIp(req);

  ws.on('message', (data, isBinary) => {
    // Binary = a media frame from the host. Fast-path relay to its viewer.
    if (isBinary) {
      if (ws.role === 'host') {
        const entry = hosts.get(ws.id);
        if (entry?.viewer && entry.viewer.readyState === entry.viewer.OPEN) {
          entry.viewer.send(data, { binary: true });
        }
      }
      return;
    }

    let msg;
    try { msg = JSON.parse(data.toString()); } catch { return; }

    switch (msg.t) {
      case 'register': {                     // host asks for an ID
        // A token (unattended hosts) maps to a stable ID; otherwise assign fresh.
        const token = typeof msg.token === 'string' && msg.token ? msg.token : null;
        let id;
        if (token && tokenToId.has(token)) {
          id = tokenToId.get(token);
          // If that ID is currently held by another live host, drop the stale one.
          const existing = hosts.get(id);
          if (existing && existing.ws !== ws) { try { existing.ws.close(); } catch {} }
        } else {
          id = newId();
          if (token) { tokenToId.set(token, id); saveIdMap(); }
        }
        ws.role = 'host';
        ws.id = id;
        hosts.set(id, { ws, viewer: null, ip: ws._ip, since: Date.now() });
        const meta = ensureClientMeta(id);
        meta.lastSeen = Date.now();
        saveAdmin();
        send(ws, { t: 'registered', id });
        console.log(`[server] host registered id=${id}${token ? ' (persistent)' : ''}`);
        break;
      }

      case 'connect': {                      // viewer -> "let me into <id>"
        const entry = hosts.get(String(msg.id));
        if (!entry) { send(ws, { t: 'rejected', reason: 'no such ID online' }); break; }
        if (entry.viewer) { send(ws, { t: 'rejected', reason: 'host is busy' }); break; }
        ws.role = 'viewer';
        const rid = randomBytes(8).toString('hex');
        pending.set(rid, ws);
        ws._rid = rid;
        ws._hostId = String(msg.id);
        // Normal path: host verifies the password locally; server never sees the truth.
        // Admin path: a viewer that proves the admin password to the RELAY is vouched
        // for (admin:true) so the host may accept without the per-client password —
        // this is what lets the Master connect from the directory without a prompt.
        const admin = msg.admin === true && isAdminPassword(msg.adminPassword);
        send(entry.ws, { t: 'connect-request', rid, password: msg.password ?? '', admin });
        break;
      }

      case 'connect-response': {             // host -> accept/reject a viewer
        const viewer = pending.get(msg.rid);
        pending.delete(msg.rid);
        if (!viewer || viewer.readyState !== viewer.OPEN) break;
        if (msg.ok) {
          const entry = hosts.get(ws.id);
          if (!entry) { send(viewer, { t: 'rejected', reason: 'host gone' }); break; }
          entry.viewer = viewer;
          viewer.id = ws.id;
          send(viewer, { t: 'connected' });
          console.log(`[server] viewer paired to host id=${ws.id}`);
        } else {
          send(viewer, { t: 'rejected', reason: 'wrong password' });
        }
        break;
      }

      // Everything else is session traffic: relay to the paired peer.
      // viewer->host: input + control.  host->viewer: {t:"screen",...} etc.
      // NOTE: guard the peer explicitly — `a?.b?.readyState === a.b.OPEN` still
      // evaluates the right-hand `a.b.OPEN` when b is null and throws.
      default: {
        if (ws.role === 'viewer' && ws.id) {
          const entry = hosts.get(ws.id);
          if (entry && entry.ws.readyState === entry.ws.OPEN) entry.ws.send(JSON.stringify(msg));
        } else if (ws.role === 'host' && ws.id) {
          const entry = hosts.get(ws.id);
          if (entry && entry.viewer && entry.viewer.readyState === entry.viewer.OPEN) {
            entry.viewer.send(JSON.stringify(msg));
          }
        }
        break;
      }
    }
  });

  ws.on('close', () => {
    if (ws.role === 'host' && ws.id) {
      const entry = hosts.get(ws.id);
      if (entry?.viewer) send(entry.viewer, { t: 'bye', reason: 'host disconnected' });
      hosts.delete(ws.id);
      ensureClientMeta(ws.id).lastSeen = Date.now();
      saveAdmin();
      console.log(`[server] host id=${ws.id} gone`);
    } else if (ws.role === 'viewer' && ws.id) {
      const entry = hosts.get(ws.id);
      if (entry) {
        entry.viewer = null;
        send(entry.ws, { t: 'bye', reason: 'viewer disconnected' });
      }
    }
    if (ws._rid) pending.delete(ws._rid);
  });

  ws.on('error', () => { /* close handler does cleanup */ });
});
