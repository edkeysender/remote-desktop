// Signaling server + Phase-0 relay.
//
// Phase 0: this process also pipes JPEG frames (host->viewer) and input JSON
// (viewer->host). From Phase 2 the media goes P2P over WebRTC and this server
// only relays SDP/ICE, then gets out of the way.
//
// It never verifies the password — the host does that. It only routes by ID.

import { WebSocketServer } from 'ws';
import { randomBytes } from 'crypto';

const PORT = process.env.PORT ? Number(process.env.PORT) : 8080;

/** @type {Map<string, {ws: import('ws').WebSocket, viewer: import('ws').WebSocket|null}>} */
const hosts = new Map();        // id -> host entry
const pending = new Map();      // requestId -> viewer ws (awaiting host's password check)

const wss = new WebSocketServer({ port: PORT });
console.log(`[server] signaling/relay listening on ws://0.0.0.0:${PORT}`);

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

wss.on('connection', (ws) => {
  ws.role = null;      // 'host' | 'viewer'
  ws.id = null;        // host id (both peers store the paired host id)

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
        const id = newId();
        ws.role = 'host';
        ws.id = id;
        hosts.set(id, { ws, viewer: null });
        send(ws, { t: 'registered', id });
        console.log(`[server] host registered id=${id}`);
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
        // Host verifies the password locally; server never sees the truth.
        send(entry.ws, { t: 'connect-request', rid, password: msg.password ?? '' });
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
      default: {
        if (ws.role === 'viewer' && ws.id) {
          const entry = hosts.get(ws.id);
          if (entry?.ws?.readyState === entry.ws.OPEN) entry.ws.send(JSON.stringify(msg));
        } else if (ws.role === 'host' && ws.id) {
          const entry = hosts.get(ws.id);
          if (entry?.viewer?.readyState === entry.viewer.OPEN) {
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
