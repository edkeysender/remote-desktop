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
  // These hrefs are the no-JS fallback; the live version comes from
  // /update/manifest.json at runtime. They pin a release deliberately, so they
  // must stay real URLs even when they lag the latest tag.
  const base = 'https://github.com/edkeysender/remote-desktop/releases/download/v0.4.3';
  assert.ok(html.includes(`${base}/RemotlerAgent-Setup-0.4.3.exe`));
  assert.ok(html.includes(`${base}/Remotler-Setup-0.4.3.exe`));
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

// ---- Phase 2: SPA integration with the landing page ----

test('the SPA loads shared design tokens instead of inlining them', async () => {
  const { html } = await get('/login');
  assert.match(html, /href="\/tokens\.css"/);
  assert.doesNotMatch(html, /:root\s*\{/);
});

test('post-auth redirects land on /app, not the marketing page', async () => {
  // Source-level guard: pushState runs in the browser, so this checks the
  // shipped source rather than the behaviour. All three auth paths (login,
  // register, accept-invite) must redirect to /app or a refresh strands the
  // user on the landing page.
  const { html } = await get('/login');
  assert.doesNotMatch(html, /pushState\(\{\},'','\/'\)/);
  assert.equal((html.match(/pushState\(\{\},'','\/app'\)/g) || []).length, 3);
});

test('the SPA opens the Create account tab on /register', async () => {
  const { html } = await get('/register');
  assert.match(html, /location\.pathname==='\/register'\?create:signin/);
});

test('landing register CTAs point at /register, sign-in at /login', async () => {
  const { html } = await get('/');
  assert.match(html, /href="\/register" data-auth-cta>Get started/);
  assert.match(html, /href="\/register" data-auth-cta>Create account/);
  assert.match(html, /href="\/login" data-auth-cta>Sign in/);
});

test('landing page resolves download links from the update manifest', async () => {
  // Source-level guard: the fetch runs in the browser. It checks the page wires
  // the manifest to the download anchors so the version can't silently rot.
  const { html } = await get('/');
  assert.match(html, /fetch\('\/update\/manifest\.json'/);
  assert.match(html, /data-dl="agentInstaller"/);
  assert.match(html, /data-dl="appInstaller"/);
  assert.match(html, /data-dl-ver/);
});

test('both pages use the R-mark favicon, and it is served', async () => {
  for (const path of ['/', '/login']) {
    const { html } = await get(path);
    assert.match(html, /rel="icon" href="\/favicon\.svg"/);
  }
  const res = await fetch(base + '/favicon.svg');
  assert.equal(res.status, 200);
  assert.match(res.headers.get('content-type') || '', /svg/);
  // The crop is the whole point: a viewBox wider than ~80 would let the
  // wordmark bleed in and the icon becomes an unreadable smear at 16px.
  assert.match(await res.text(), /viewBox="0 0 80 80"/);
});
