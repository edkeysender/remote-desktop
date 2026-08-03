#!/usr/bin/env node
// AllViewer MCP server — exposes one organization's fleet to an MCP client.
// Auth: an org API token (create it in the web app → MCP tab).
//   ALLVIEWER_URL=http://<host>:<port>   ALLVIEWER_TOKEN=hk_...
// Tools: get_computers, get_online_computers, get_tasks, kill_task.

import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { ListToolsRequestSchema, CallToolRequestSchema } from '@modelcontextprotocol/sdk/types.js';

const BASE = (process.env.ALLVIEWER_URL || 'https://allviewer.tech').replace(/\/$/, '');
const TOKEN = process.env.ALLVIEWER_TOKEN || '';

async function api(path, opts = {}) {
  const r = await fetch(BASE + path, {
    ...opts,
    headers: { Authorization: 'Bearer ' + TOKEN, 'Content-Type': 'application/json', ...(opts.headers || {}) },
  });
  const text = await r.text();
  let data; try { data = JSON.parse(text); } catch { data = text; }
  if (!r.ok) throw new Error((data && data.error) || ('HTTP ' + r.status));
  return data;
}

// Accept a computer by device token, relay ID, or name.
async function resolveDevice(id) {
  const cs = await api('/api/computers');
  const c = cs.find((x) => x.deviceToken === id || x.relayId === id || x.name === id);
  if (!c) throw new Error('device not found: ' + id);
  return c;
}

const tools = [
  { name: 'get_computers', description: 'List all computers in the organization (name, device ID, relay ID, online, group).', inputSchema: { type: 'object', properties: {} } },
  { name: 'get_online_computers', description: 'List only the computers that are currently online.', inputSchema: { type: 'object', properties: {} } },
  { name: 'get_tasks', description: 'List running processes (task manager) on an online computer.', inputSchema: { type: 'object', properties: { device: { type: 'string', description: 'computer name, device ID, or relay ID' } }, required: ['device'] } },
  { name: 'kill_task', description: 'Kill a process by PID on an online computer.', inputSchema: { type: 'object', properties: { device: { type: 'string' }, pid: { type: 'number' } }, required: ['device', 'pid'] } },
];

const server = new Server({ name: 'allviewer', version: '0.1.0' }, { capabilities: { tools: {} } });
server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools }));
server.setRequestHandler(CallToolRequestSchema, async (req) => {
  const { name, arguments: args = {} } = req.params;
  try {
    let out;
    if (name === 'get_computers') out = await api('/api/computers');
    else if (name === 'get_online_computers') out = (await api('/api/computers')).filter((c) => c.online);
    else if (name === 'get_tasks') { const c = await resolveDevice(args.device); out = await api('/api/computers/' + encodeURIComponent(c.deviceToken) + '/tasks'); }
    else if (name === 'kill_task') { const c = await resolveDevice(args.device); out = await api('/api/computers/' + encodeURIComponent(c.deviceToken) + '/kill', { method: 'POST', body: JSON.stringify({ pid: args.pid }) }); }
    else throw new Error('unknown tool: ' + name);
    return { content: [{ type: 'text', text: JSON.stringify(out, null, 2) }] };
  } catch (e) {
    return { content: [{ type: 'text', text: 'Error: ' + e.message }], isError: true };
  }
});

await server.connect(new StdioServerTransport());
console.error('[allviewer-mcp] ready ->', BASE);
