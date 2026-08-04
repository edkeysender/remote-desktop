# Landing page for remotler.com

**Date:** 2026-08-04
**Branch:** `landing-page` (worktree at `../remote-desktop-landing`, based on `master`)

## Problem

`https://remotler.com/` serves the control-plane SPA. `boot()` in
`server/public/index.html` tries `/api/me`, and on failure renders `renderAuth()` — the
Sign in / Create account card. A visitor who has never heard of Remotler is therefore
asked for credentials before being told what the product is.

We want a marketing page at `/` that explains the product and routes people into the
existing SPA.

## Decisions

Settled during brainstorming:

- **Registration stays open.** `/api/register` is currently ungated and will remain so.
  The landing page gets both Login and Register calls to action.
- **Standard marketing page.** Hero, how-it-works, features, download, footer. No
  pricing, no competitor comparison, no FAQ.
- **Logged-in visitors get a swapped CTA**, resolved client-side rather than by a
  server-side redirect, so `/` stays static and cacheable.
- **Download links point at GitHub Releases.** The alternative — serving from
  `/update/` on our own domain — depends on an in-progress GitHub-Releases fallback
  that is not yet committed. Revisit once that lands.

## Constraint: concurrent work

Another session is actively editing `server/index.js`, `server/public/index.html`, and
an untracked `server/lib/releases.js` in the primary clone. This design therefore
avoids `server/public/index.html` entirely, and the work is split into two phases. All
Phase 1 work happens in an isolated git worktree so the other session's tree is never
disturbed.

## Architecture

### Phase 1 — landing page and routing

Touches only new files plus `server/lib/web.js`, none of which the other session holds.

**`server/public/landing.html`** (new) — the marketing page. Self-contained HTML and
CSS, plus roughly fifteen lines of JavaScript for the logged-in CTA swap. No build
step and no framework, matching how `index.html` is already written.

**`server/public/tokens.css`** (new) — the `:root{…}` custom-property block lifted from
`index.html`: `--accent`, `--grad`, `--ink`, `--paper`, `--line`, the radii and the
shadow. Only `landing.html` links it in this phase; `index.html` keeps its inline copy
until Phase 2 can dedupe them.

**`server/lib/web.js`** — the catch-all at lines 371–372 currently maps `/`, `/login`,
`/invite/:token`, `/app` and `/app/*` all to `index.html`. It splits:

```
GET /                                              → landing.html
GET /login, /register, /app, /app/*, /invite/:token → index.html
```

`express.static(PUBLIC_DIR)` on line 369 already serves `logo.png` and picks up
`tokens.css` with no change.

### Phase 2 — SPA integration (deferred)

Blocked until `server/public/index.html` is free. Three edits:

1. `renderAuth()` opens the Create account tab when `location.pathname === '/register'`,
   so the landing's Register CTA lands on the right form.
2. The three `history.pushState({},'','/')` calls (lines 163, 167, 176) become `'/app'`.
   Without this, signing in rewrites the URL to the landing page: the dashboard still
   renders, but refreshing drops the user onto marketing.
3. `index.html` links `tokens.css` and drops its inline `:root` block.

Phase 1 is coherent without Phase 2. Until it lands, the Register CTA points at
`/login`, where the visitor clicks the Create account tab themselves — one extra click,
nothing broken.

The `/register` route is nevertheless added in Phase 1, even though nothing links to it
yet. Serving the SPA there is harmless on its own, and putting it in now means Phase 2
is a change to `index.html` alone, with no second edit to `web.js` — which matters when
the blocking factor is contention over that one file. Phase 2 flips the CTA's `href`
from `/login` to `/register` at the same time.

## Logged-in CTA swap

The session is a `same-origin` cookie, not a token in `localStorage` (see the `fetch`
in `index.html` line 129), so the landing page cannot read auth state synchronously. On
load it calls `/api/me`:

- **200** — replace the Login and Register buttons with a single **Open dashboard** →
  `/app`.
- **anything else** — do nothing; the static markup already shows the logged-out CTAs.

Because the logged-out state is the default, there is no flash of the wrong control for
anonymous visitors, who are the common case.

## Page content

Every claim traces to `README.md` or `docs/ARCHITECTURE.md`. No invented benchmarks,
customers, or certifications.

| Section | Content |
|---|---|
| Header | Logo, Login, Register |
| Hero | Positioning line, sub-line, both CTAs |
| How it works | Install the agent → share the 9-digit ID and password → connect and control |
| Features | Direct peer-to-peer WebRTC · DTLS-SRTP encryption · the relay never sees pixels or input · fleet management (groups, users, enrollment, sessions, activity) · self-hosted · native Windows installers that bundle the .NET runtime |
| Download | Two buttons to v0.3.1 release assets: `RemotlerAgent-Setup-0.3.1.exe` (98 MB, the machine being controlled) and `Remotler-Setup-0.3.1.exe` (71 MB, the operator's machine) |
| Footer | Copyright, Login, Register, GitHub |

Download URLs follow
`https://github.com/edkeysender/remote-desktop/releases/download/v0.3.1/<asset>`. The
version is hardcoded and needs a manual bump each release — the cost of not depending on
the unfinished `/update/` fallback.

### Visual language

Inherits the SPA's palette so the two do not read as different products: accent
`#5B5BF5`, the purple-to-cyan `--grad`, ink `#0B0C15` on paper `#F5F6F9`. Typography is
Schibsted Grotesk for headings, Inter for body, JetBrains Mono for the ID/password
sample — loaded from Google Fonts exactly as `index.html` does today. Responsive to
mobile widths.

## Testing

Run the server locally with `npm start` and confirm:

1. `/` serves the landing page.
2. `/login` and `/invite/<token>` still serve the SPA.
3. `/register` serves the SPA (opening on Sign in until Phase 2).
4. `/app` serves the SPA.
5. Signing in still reaches the dashboard.
6. With a valid session cookie, the landing CTA becomes **Open dashboard**; without one,
   Login and Register render.
7. Both download links resolve to real release assets (`curl -sIL`, expect a final 200).

Deploy by merging to master, then on the droplet:
`cd /opt/remotler && sudo -u remotler git pull && systemctl restart remotler-signal`.
Verify against `https://remotler.com/` afterwards.

## Out of scope

- Pricing, competitor comparison, FAQ, testimonials.
- Any change to `/admin` or `/platform`.
- Gating `/api/register`.
- The empty `/update/` directory on the droplet — the other session's GitHub-Releases
  fallback addresses that.
- The macOS viewer client on `mac-viewer-client`.
