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

test('landing page covers the required sections', async () => {
  const { html } = await get('/');
  assert.match(html, /How it works/i);
  assert.match(html, /peer-to-peer/i);
  assert.match(html, /self-hosted/i);
});

test('landing page links to both installers at the pinned version', async () => {
  const { html } = await get('/');
  const bases = 'https://github.com/edkeysender/remote-desktop/releases/download/v0.3.1';
  assert.ok(html.includes(`${bases}/RemotlerAgent-Setup-0.3.1.exe`));
  assert.ok(html.includes(`${bases}/Remotler-Setup-0.3.1.exe`));
});

test('landing page offers both sign-in and register entry points', async () => {
  const { html } = await get('/');
  assert.match(html, /href="\/login"/);
  assert.match(html, /data-auth-cta/);
  assert.match(html, /data-dash-cta/);
});

test('landing page loads the shared design tokens', async () => {
  const { html } = await get('/');
  assert.match(html, /href="\/tokens\.css"/);
  const res = await fetch(base + '/tokens.css');
  assert.equal(res.status, 200);
  assert.match(await res.text(), /--accent:\s*#5B5BF5/);
});

test('landing page resolves auth state against /api/me', async () => {
  const { html } = await get('/');
  assert.match(html, /fetch\('\/api\/me'/);
  assert.match(html, /credentials:\s*'same-origin'/);
});

// Source-level guard, not behavioural coverage: there is no DOM in this test
// environment (jsdom is not permitted), so we can't assert that
// [data-dash-cta][hidden] actually renders as display:none. What we can check
// is that the CSS rule the hiding depends on is still present in the served
// markup — author `display` rules (e.g. .btn) beat the user-agent stylesheet's
// [hidden]{display:none}, so without this rule the dashboard CTA would be
// visible to logged-out visitors again.
test('landing page CSS still forces [hidden] elements to display:none', async () => {
  const { html } = await get('/');
  assert.match(html, /\[hidden\]\s*\{\s*display:\s*none\s*!important\s*\}/);
});
