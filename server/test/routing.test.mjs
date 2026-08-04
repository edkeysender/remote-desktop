// Route tests for the public web app. buildWebApp() is imported dynamically because
// lib/auth.js and lib/store.js read DATA_DIR at module load — auth.js writes a session
// secret there — so the env var has to be set before the import runs.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import express from 'express';

const DATA_DIR = mkdtempSync(join(tmpdir(), 'remotler-test-'));
process.env.DATA_DIR = DATA_DIR;

let server;
let base;

before(async () => {
  const { buildWebApp } = await import('../lib/web.js');
  // buildWebApp() returns an express.Router(), not a full app — it relies on
  // being mounted inside one (see index.js: app.use(buildWebApp(...))), which
  // is what wires up res.sendFile/res.json/etc. via Express's init middleware.
  // Mount it the same way here so the harness exercises the real request path.
  const app = express();
  app.use(buildWebApp({
    relayStatus: () => ({}),
    sendCommand: () => {},
    onlinePeer: () => null,
  }));
  server = createServer(app);
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  base = `http://127.0.0.1:${server.address().port}`;
});

after(() => {
  server?.close();
  rmSync(DATA_DIR, { recursive: true, force: true });
});

const get = async (path) => {
  const res = await fetch(base + path);
  return { status: res.status, html: await res.text() };
};

test('/ serves the landing page, not the SPA', async () => {
  const { status, html } = await get('/');
  assert.equal(status, 200);
  assert.match(html, /data-page="landing"/);
});

test('/login serves the SPA shell', async () => {
  const { status, html } = await get('/login');
  assert.equal(status, 200);
  assert.match(html, /<div id="app">/);
  assert.doesNotMatch(html, /data-page="landing"/);
});

test('/register serves the SPA shell', async () => {
  const { status, html } = await get('/register');
  assert.equal(status, 200);
  assert.match(html, /<div id="app">/);
});

test('/app serves the SPA shell', async () => {
  const { status, html } = await get('/app');
  assert.equal(status, 200);
  assert.match(html, /<div id="app">/);
});

test('/invite/:token still serves the SPA shell', async () => {
  const { status, html } = await get('/invite/abc123');
  assert.equal(status, 200);
  assert.match(html, /<div id="app">/);
});
