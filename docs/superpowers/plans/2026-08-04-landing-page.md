# Landing Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serve a marketing page at `https://remotler.com/` that explains the product and routes visitors into the existing control-plane SPA, instead of confronting them with a login form.

**Architecture:** A new static `landing.html` is served at `/` while the SPA moves to `/login`, `/register`, `/app` and `/invite/:token`. The only existing file touched is `server/lib/web.js`, whose SPA catch-all route splits in two. Shared design tokens move into `tokens.css` so the landing page and the app cannot drift apart visually. Auth state is resolved client-side by one `/api/me` call, keeping `/` static and cacheable.

**Tech Stack:** Node 20+ ESM, Express 4, plain HTML/CSS/JS with no build step, `node:test` for tests.

## Global Constraints

- **Work only in the worktree `~/remote-desktop-landing`, on branch `landing-page`.** The primary clone at `~/remote-desktop` has another session working in it.
- **Never modify `server/public/index.html`, `server/index.js`, or `server/lib/releases.js`.** Another session holds all three with uncommitted changes. Touching them risks losing that work. This is why the `/register` tab and the post-login redirect are deferred to Phase 2 and are *not* in this plan.
- **No new npm dependencies.** Tests use the built-in `node:test` runner and global `fetch`. The droplet runs Node v20.20.2; local dev is v22.9.0. Both have these built in.
- The server is ESM (`"type": "module"` in `server/package.json`). Use `import`, never `require`.
- **Release version is pinned to `v0.3.1`.** Asset URLs are exactly `https://github.com/edkeysender/remote-desktop/releases/download/v0.3.1/<asset>`, with assets `RemotlerAgent-Setup-0.3.1.exe` (98 MB) and `Remotler-Setup-0.3.1.exe` (71 MB).
- Design token values are copied **verbatim** from the `:root` block in `server/public/index.html`. Do not invent or adjust colours.
- Fonts: Schibsted Grotesk for headings, Inter for body, JetBrains Mono for monospace — loaded from Google Fonts with the same `<link>` the SPA already uses.
- `server/lib/auth.js` and `server/lib/store.js` read `process.env.DATA_DIR` **at module load**, and `auth.js` writes a secret file there. Tests must set `DATA_DIR` to a temp directory *before* dynamically importing `web.js`.

---

## File Structure

| File | Responsibility |
|---|---|
| `server/public/landing.html` (create) | The entire marketing page: markup, page-specific styles, CTA-swap script |
| `server/public/tokens.css` (create) | Shared design tokens (`:root` custom properties) |
| `server/lib/web.js` (modify, lines 371–372) | Route split: `/` → landing, SPA routes → `index.html` |
| `server/test/routing.test.mjs` (create) | Route and content tests |
| `server/package.json` (modify) | Add a `test` script |

---

### Task 1: Route split with a test harness

Establishes the test harness and proves `/` and the SPA routes are served from different files.

**Files:**
- Create: `server/test/routing.test.mjs`
- Create: `server/public/landing.html` (minimal placeholder; Task 2 replaces the body)
- Modify: `server/lib/web.js:371-372`
- Modify: `server/package.json`
- Test: `server/test/routing.test.mjs`

**Interfaces:**
- Consumes: `buildWebApp({ relayStatus, sendCommand, onlinePeer })` from `server/lib/web.js`, which returns an Express app.
- Produces: `server/public/landing.html` carrying the marker attribute `data-page="landing"` on its `<html>` element. Tasks 2 and 3 rely on that attribute, and on `npm test` existing.

- [ ] **Step 1: Install dependencies in the worktree**

The worktree has no `node_modules`.

```bash
cd ~/remote-desktop-landing/server && npm install
```

- [ ] **Step 2: Add the test script**

In `server/package.json`, add `test` to the `scripts` object so it reads:

```json
  "scripts": {
    "start": "node index.js",
    "test": "node --test test/"
  },
```

- [ ] **Step 3: Write the failing test**

Create `server/test/routing.test.mjs`:

```js
// Route tests for the public web app. buildWebApp() is imported dynamically because
// lib/auth.js and lib/store.js read DATA_DIR at module load — auth.js writes a session
// secret there — so the env var has to be set before the import runs.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const DATA_DIR = mkdtempSync(join(tmpdir(), 'remotler-test-'));
process.env.DATA_DIR = DATA_DIR;

let server;
let base;

before(async () => {
  const { buildWebApp } = await import('../lib/web.js');
  const app = buildWebApp({
    relayStatus: () => ({}),
    sendCommand: () => {},
    onlinePeer: () => null,
  });
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
```

- [ ] **Step 4: Run the test to verify it fails**

```bash
cd ~/remote-desktop-landing/server && npm test
```

Expected: the `/` and `/register` tests FAIL. `/` currently serves the SPA, so `data-page="landing"` is absent; `/register` is not a route yet, so it 404s.

- [ ] **Step 5: Create the placeholder landing page**

Create `server/public/landing.html`. Task 2 replaces the body — this exists only to make the route real:

```html
<!doctype html>
<html lang="en" data-page="landing">
<head>
<meta charset="utf-8">
<title>Remotler</title>
</head>
<body>
<a href="/login">Sign in</a>
</body>
</html>
```

- [ ] **Step 6: Split the route**

Two edits in `server/lib/web.js`, and the first is essential.

**6a.** `serve-static` defaults `index` to `'index.html'`, so `app.use(express.static(...))`
answers `GET /` with `public/index.html` *before* any route handler runs. Today that is
invisible — the route below returns the same file — but it would silently shadow the new
landing route. Turn it off. Change line 369 from:

```js
  app.use(express.static(PUBLIC_DIR));
```

to:

```js
  // index:false — GET / must reach the landing route below, not be intercepted here.
  app.use(express.static(PUBLIC_DIR, { index: false }));
```

**6b.** Replace lines 371–372:

```js
  app.get(['/', '/login', '/invite/:token', '/app', '/app/*'], (_req, res) =>
    res.sendFile(join(PUBLIC_DIR, 'index.html')));
```

with:

```js
  // The public marketing page. Everything below it is the signed-in application.
  app.get('/', (_req, res) => res.sendFile(join(PUBLIC_DIR, 'landing.html')));
  // Client-side routes fall back to the SPA shell.
  app.get(['/login', '/register', '/invite/:token', '/app', '/app/*'], (_req, res) =>
    res.sendFile(join(PUBLIC_DIR, 'index.html')));
```

If the `/` test still fails after this, 6a was missed — that is the only way the static
middleware can still be winning.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
cd ~/remote-desktop-landing/server && npm test
```

Expected: all 5 tests PASS.

- [ ] **Step 8: Commit**

```bash
cd ~/remote-desktop-landing
git add server/test/routing.test.mjs server/public/landing.html server/lib/web.js server/package.json
git commit -m "Serve a landing page at / and move the SPA to /login, /register, /app"
```

---

### Task 2: Design tokens and the full landing page

**Files:**
- Create: `server/public/tokens.css`
- Modify: `server/public/landing.html` (full replacement)
- Modify: `server/test/routing.test.mjs` (append content tests)

**Interfaces:**
- Consumes: the `data-page="landing"` marker and `npm test` from Task 1.
- Produces: markup with `[data-auth-cta]` on the logged-out buttons and `[data-dash-cta]` on the hidden "Open dashboard" link. Task 3's script targets exactly those two attributes.

- [ ] **Step 1: Write the failing content tests**

Append to `server/test/routing.test.mjs`:

```js
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
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd ~/remote-desktop-landing/server && npm test
```

Expected: the 4 new tests FAIL — the placeholder page has none of this content and `tokens.css` does not exist.

- [ ] **Step 3: Create the shared design tokens**

Create `server/public/tokens.css`. Values are copied verbatim from the `:root` block in `index.html`:

```css
/* Shared design tokens for the landing page and the control-plane app.
   index.html still carries its own inline copy; Phase 2 replaces that with a link
   to this file so the two cannot drift apart. */
:root{
  --ink:#0B0C15; --ink-soft:#1A1C2B; --paper:#F5F6F9; --card:#FFFFFF; --line:#E8E9F0;
  --text:#0B0C15; --muted:#6E7185; --accent:#5B5BF5; --accent-soft:#EEEEFE;
  --grad:linear-gradient(135deg,#5B5BF5 0%,#9D5CFF 55%,#3EC8FF 120%);
  --ok:#1FC98B; --warn:#FFAA1D; --danger:#F5484D;
  --r-lg:22px; --r-md:14px; --r-pill:999px;
  --shadow:0 1px 2px rgba(11,12,21,.04),0 8px 24px rgba(11,12,21,.06);
}
```

- [ ] **Step 4: Write the landing page**

Replace `server/public/landing.html` entirely:

```html
<!doctype html>
<html lang="en" data-page="landing">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Remotler — remote control for your whole fleet</title>
<meta name="description" content="Direct peer-to-peer remote desktop for Windows. Encrypted end to end, self-hosted on your own server.">
<link rel="icon" href="/logo.png">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Schibsted+Grotesk:wght@500;700;800&family=Inter:wght@400;500;600&family=JetBrains+Mono:wght@500;600&display=swap" rel="stylesheet">
<link rel="stylesheet" href="/tokens.css">
<style>
*{margin:0;padding:0;box-sizing:border-box}
html{font-size:15px;scroll-behavior:smooth}
body{font-family:'Inter',system-ui,sans-serif;background:var(--paper);color:var(--text);-webkit-font-smoothing:antialiased;line-height:1.6}
h1,h2,h3{font-family:'Schibsted Grotesk',system-ui,sans-serif;letter-spacing:-.02em;line-height:1.15}
a{color:var(--accent);text-decoration:none}
:focus-visible{outline:2px solid var(--accent);outline-offset:3px;border-radius:6px}
.wrap{max-width:1080px;margin:0 auto;padding:0 24px}

/* nav */
.nav{position:sticky;top:0;z-index:10;background:rgba(245,246,249,.85);backdrop-filter:blur(12px);border-bottom:1px solid var(--line)}
.nav .wrap{display:flex;align-items:center;justify-content:space-between;height:64px;gap:16px}
.logo{display:flex;align-items:center;gap:10px;font-family:'Schibsted Grotesk',sans-serif;font-weight:800;font-size:1.1rem;color:var(--ink)}
.logo img{height:26px;width:auto;display:block}
.nav-cta{display:flex;align-items:center;gap:10px}

/* buttons */
.btn{display:inline-block;border-radius:var(--r-pill);padding:10px 20px;font-weight:600;font-size:.9rem;border:1px solid var(--line);background:#fff;color:var(--text);transition:transform .15s,box-shadow .15s}
.btn:hover{transform:translateY(-1px);box-shadow:var(--shadow)}
.btn.primary{background:var(--ink);border-color:var(--ink);color:#fff}
.btn.lg{padding:14px 28px;font-size:1rem}

/* hero */
.hero{padding:96px 0 80px;text-align:center;position:relative;overflow:hidden}
.hero::before{content:"";position:absolute;inset:-40% 20% auto;height:520px;background:var(--grad);filter:blur(120px);opacity:.16;pointer-events:none}
.hero h1{font-size:clamp(2.2rem,5.5vw,3.6rem);font-weight:800;position:relative}
.hero p{margin:20px auto 0;max-width:620px;font-size:1.12rem;color:var(--muted);position:relative}
.hero .actions{margin-top:34px;display:flex;gap:12px;justify-content:center;flex-wrap:wrap;position:relative}
.pill{display:inline-block;margin-bottom:20px;padding:6px 14px;border-radius:var(--r-pill);background:var(--accent-soft);color:var(--accent);font-size:.78rem;font-weight:600;letter-spacing:.02em;position:relative}

/* sections */
section{padding:72px 0}
.eyebrow{font-size:.74rem;font-weight:700;letter-spacing:.12em;text-transform:uppercase;color:var(--muted);margin-bottom:12px}
.sec-title{font-size:clamp(1.6rem,3vw,2.1rem);font-weight:700;margin-bottom:12px}
.sec-lead{color:var(--muted);max-width:620px;margin-bottom:40px}

/* steps */
.steps{display:grid;grid-template-columns:repeat(3,1fr);gap:24px;counter-reset:step}
.step{background:var(--card);border:1px solid var(--line);border-radius:var(--r-lg);padding:28px;box-shadow:var(--shadow)}
.step::before{counter-increment:step;content:counter(step);display:grid;place-items:center;width:34px;height:34px;border-radius:var(--r-pill);background:var(--ink);color:#fff;font-family:'JetBrains Mono',monospace;font-weight:600;font-size:.9rem;margin-bottom:16px}
.step h3{font-size:1.05rem;margin-bottom:8px}
.step p{color:var(--muted);font-size:.94rem}
.step code{font-family:'JetBrains Mono',monospace;font-size:.86rem;background:var(--accent-soft);color:var(--accent);padding:2px 6px;border-radius:6px}

/* features */
.features{display:grid;grid-template-columns:repeat(3,1fr);gap:20px}
.feature{background:var(--card);border:1px solid var(--line);border-radius:var(--r-md);padding:24px}
.feature h3{font-size:1rem;margin-bottom:8px}
.feature p{color:var(--muted);font-size:.92rem}

/* download */
.download{background:var(--ink);border-radius:var(--r-lg);padding:48px;color:#fff;text-align:center}
.download h2{font-size:clamp(1.5rem,3vw,2rem);margin-bottom:10px}
.download > p{color:#A9ACC4;max-width:560px;margin:0 auto 32px}
.dl-grid{display:grid;grid-template-columns:repeat(2,1fr);gap:16px;max-width:720px;margin:0 auto;text-align:left}
.dl{display:block;background:var(--ink-soft);border:1px solid #2C2F44;border-radius:var(--r-md);padding:22px;color:#fff;transition:transform .15s,border-color .15s}
.dl:hover{transform:translateY(-2px);border-color:var(--accent)}
.dl strong{display:block;font-family:'Schibsted Grotesk',sans-serif;font-size:1rem;margin-bottom:6px}
.dl span{display:block;color:#A9ACC4;font-size:.88rem}
.dl em{display:block;margin-top:12px;font-style:normal;font-family:'JetBrains Mono',monospace;font-size:.78rem;color:var(--accent)}

/* footer */
footer{border-top:1px solid var(--line);padding:32px 0;color:var(--muted);font-size:.88rem}
footer .wrap{display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap}
footer nav{display:flex;gap:20px}

@media(max-width:860px){
  .steps,.features,.dl-grid{grid-template-columns:1fr}
  .hero{padding:64px 0 56px}
  section{padding:52px 0}
  .download{padding:32px 20px}
}
</style>
</head>
<body>

<header class="nav">
  <div class="wrap">
    <a class="logo" href="/"><img src="/logo.png" alt="">Remotler</a>
    <div class="nav-cta">
      <a class="btn" href="/login" data-auth-cta>Sign in</a>
      <a class="btn primary" href="/login" data-auth-cta>Get started</a>
      <a class="btn primary" href="/app" data-dash-cta hidden>Open dashboard</a>
    </div>
  </div>
</header>

<main>
  <section class="hero">
    <div class="wrap">
      <span class="pill">Self-hosted remote desktop</span>
      <h1>Remote control for your whole fleet</h1>
      <p>Screen and input travel straight between the two machines over WebRTC — encrypted end to end, on a relay you run yourself.</p>
      <div class="actions">
        <a class="btn primary lg" href="/login" data-auth-cta>Get started</a>
        <a class="btn lg" href="#download">Download for Windows</a>
      </div>
    </div>
  </section>

  <section>
    <div class="wrap">
      <p class="eyebrow">How it works</p>
      <h2 class="sec-title">Three steps to a remote session</h2>
      <p class="sec-lead">No port forwarding, no VPN, no per-seat licence.</p>
      <div class="steps">
        <div class="step">
          <h3>Install the agent</h3>
          <p>Run the agent installer on the PC you want to reach. It shows a nine-digit ID and a password.</p>
        </div>
        <div class="step">
          <h3>Share the ID</h3>
          <p>Hand the ID and password to whoever is connecting, or enrol the machine into your organisation so it appears in the dashboard.</p>
        </div>
        <div class="step">
          <h3>Connect and control</h3>
          <p>Open Remotler, enter the ID and password. The remote screen appears and your mouse and keyboard drive it. <code>Ctrl+Alt+End</code> ends the session.</p>
        </div>
      </div>
    </div>
  </section>

  <section>
    <div class="wrap">
      <p class="eyebrow">Why Remotler</p>
      <h2 class="sec-title">Built so the server stays out of the way</h2>
      <p class="sec-lead">The relay brokers the introduction and then steps aside.</p>
      <div class="features">
        <div class="feature">
          <h3>Direct peer-to-peer</h3>
          <p>Video and input flow straight between the two machines over WebRTC. The server only exchanges the initial handshake.</p>
        </div>
        <div class="feature">
          <h3>Encrypted end to end</h3>
          <p>DTLS-SRTP secures the media and data channels. The relay never sees pixels or keystrokes.</p>
        </div>
        <div class="feature">
          <h3>Works through NAT</h3>
          <p>STUN finds a direct path where one exists, with a TURN relay as fallback for networks that refuse to hole-punch.</p>
        </div>
        <div class="feature">
          <h3>Fleet management</h3>
          <p>Groups, users, enrolment tokens, session history and an activity log — all in one dashboard.</p>
        </div>
        <div class="feature">
          <h3>Self-hosted</h3>
          <p>Runs on your own server. Your devices, your data, your relay, your rules.</p>
        </div>
        <div class="feature">
          <h3>Native Windows apps</h3>
          <p>Single-file installers that bundle the .NET runtime. Nothing to install first.</p>
        </div>
      </div>
    </div>
  </section>

  <section id="download">
    <div class="wrap">
      <div class="download">
        <h2>Download for Windows</h2>
        <p>Install the agent on machines you want to reach, and Remotler on the machine you drive from.</p>
        <div class="dl-grid">
          <a class="dl" href="https://github.com/edkeysender/remote-desktop/releases/download/v0.3.1/RemotlerAgent-Setup-0.3.1.exe">
            <strong>Remotler Agent</strong>
            <span>For the PC being controlled. Installs as a service for unattended access; requires admin.</span>
            <em>v0.3.1 · 98 MB</em>
          </a>
          <a class="dl" href="https://github.com/edkeysender/remote-desktop/releases/download/v0.3.1/Remotler-Setup-0.3.1.exe">
            <strong>Remotler</strong>
            <span>For your own PC. Per-user install, no admin rights needed.</span>
            <em>v0.3.1 · 71 MB</em>
          </a>
        </div>
      </div>
    </div>
  </section>
</main>

<footer>
  <div class="wrap">
    <span>© Remotler</span>
    <nav>
      <a href="/login" data-auth-cta>Sign in</a>
      <a href="/login" data-auth-cta>Create account</a>
      <a href="/app" data-dash-cta hidden>Dashboard</a>
      <a href="https://github.com/edkeysender/remote-desktop">GitHub</a>
    </nav>
  </div>
</footer>

</body>
</html>
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd ~/remote-desktop-landing/server && npm test
```

Expected: all 9 tests PASS.

- [ ] **Step 6: Commit**

```bash
cd ~/remote-desktop-landing
git add server/public/tokens.css server/public/landing.html server/test/routing.test.mjs
git commit -m "Build out the landing page and extract shared design tokens"
```

---

### Task 3: Swap the CTA for signed-in visitors

**Files:**
- Modify: `server/public/landing.html` (append a script before `</body>`)
- Modify: `server/test/routing.test.mjs` (append one test)

**Interfaces:**
- Consumes: `[data-auth-cta]` and `[data-dash-cta]` from Task 2.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

Append to `server/test/routing.test.mjs`:

```js
test('landing page resolves auth state against /api/me', async () => {
  const { html } = await get('/');
  assert.match(html, /fetch\('\/api\/me'/);
  assert.match(html, /credentials:\s*'same-origin'/);
});
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd ~/remote-desktop-landing/server && npm test
```

Expected: the new test FAILS — there is no script on the page yet.

- [ ] **Step 3: Add the CTA-swap script**

In `server/public/landing.html`, insert immediately before the closing `</body>` tag:

```html
<script>
// The session is an httpOnly same-origin cookie, so auth state can't be read
// synchronously. The page ships in its logged-out state — the common case, and the
// reason there's no flash — and upgrades in place if /api/me answers.
fetch('/api/me', { credentials: 'same-origin' })
  .then((r) => (r.ok ? r.json() : null))
  .then((me) => {
    if (!me) return;
    document.querySelectorAll('[data-auth-cta]').forEach((el) => el.remove());
    document.querySelectorAll('[data-dash-cta]').forEach((el) => { el.hidden = false; });
  })
  .catch(() => {});   // offline or server down: the logged-out CTAs remain correct
</script>
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd ~/remote-desktop-landing/server && npm test
```

Expected: all 10 tests PASS.

- [ ] **Step 5: Commit**

```bash
cd ~/remote-desktop-landing
git add server/public/landing.html server/test/routing.test.mjs
git commit -m "Show Open dashboard on the landing page when already signed in"
```

---

### Task 4: Verify against a running server, then deploy

**Files:** none modified — this is verification.

- [ ] **Step 1: Start the server locally**

```bash
cd ~/remote-desktop-landing/server && DATA_DIR=/tmp/remotler-local PORT=8099 npm start
```

Leave it running; use a second terminal for the checks below.

- [ ] **Step 2: Check every route by hand**

```bash
for p in / /login /register /app /invite/abc /health; do
  printf "%-12s %s\n" "$p" "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8099$p)"
done
```

Expected: all `200`.

- [ ] **Step 3: Confirm / and /login serve different pages**

```bash
curl -s http://127.0.0.1:8099/      | grep -c 'data-page="landing"'   # expect 1
curl -s http://127.0.0.1:8099/login | grep -c 'data-page="landing"'   # expect 0
```

- [ ] **Step 4: Confirm the download links are real**

These hit GitHub, so they are checked here rather than in the test suite, which must stay offline-safe.

```bash
for a in RemotlerAgent-Setup-0.3.1.exe Remotler-Setup-0.3.1.exe; do
  printf "%-32s %s\n" "$a" "$(curl -sIL -o /dev/null -w '%{http_code}' \
    https://github.com/edkeysender/remote-desktop/releases/download/v0.3.1/$a)"
done
```

Expected: `200` for both.

- [ ] **Step 5: Look at the page**

Open `http://127.0.0.1:8099/` in a browser. Confirm the hero, three steps, six feature cards, dark download panel and footer all render; that "Sign in" and "Get started" reach the SPA; and that the layout holds when the window is narrowed to phone width. Stop the server when done.

- [ ] **Step 6: Merge to master**

```bash
cd ~/remote-desktop-landing
git checkout master && git merge --no-ff landing-page -m "Add a marketing landing page at /"
git push origin master
```

- [ ] **Step 7: Deploy and verify live**

```bash
ssh -i ~/.ssh/remotler_ed25519 root@174.138.5.154 \
  'cd /opt/remotler && sudo -u remotler git pull && systemctl restart remotler-signal'

curl -s --resolve remotler.com:443:174.138.5.154 https://remotler.com/ | grep -c 'data-page="landing"'
curl -s -o /dev/null -w '%{http_code}\n' --resolve remotler.com:443:174.138.5.154 https://remotler.com/login
```

Expected: `1`, then `200`.

- [ ] **Step 8: Clean up the worktree**

Only once the deploy is verified:

```bash
cd ~/remote-desktop && git worktree remove ../remote-desktop-landing
```

---

## Deferred to Phase 2

Blocked on `server/public/index.html`, which another session is holding. Not part of this plan:

1. `renderAuth()` opens the Create account tab when `location.pathname === '/register'`, and the landing page's register CTAs change from `/login` to `/register`.
2. The three `history.pushState({},'','/')` calls (lines 163, 167, 176) become `'/app'`. **This is the one that matters** — until it lands, signing in rewrites the URL to `/`, so the dashboard renders but a refresh drops the user on the marketing page.
3. `index.html` links `/tokens.css` and drops its inline `:root` block.
