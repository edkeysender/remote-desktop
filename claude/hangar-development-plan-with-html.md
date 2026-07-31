# Hangar — Full Development Plan
**Remote control & fleet management platform for simulator operators**
Version 1.0 · July 2026 · Target platforms: Windows (launch), macOS (Phase 2+), Web dashboards (all phases)

---

## 1. Product Summary

Hangar is a multi-tenant remote access and fleet management platform (TeamViewer/Iperius-class) sold to simulator clients. It ships in two deployment models: **Cloud** (SaaS, hosted by us) and **Self-hosted** (single deployable stack for air-gapped/defence clients). Core entities: `Organization → Group → Device`, with users holding group-scoped roles (Org Owner, Org Admin, Technician, Operator, Viewer/Auditor).

The product consists of four components:

| Component | What it is | Platforms |
|---|---|---|
| **Agent** | Service on the controlled machine: capture, encode, input injection, monitoring | Windows 10/11 → macOS 13+ |
| **Desktop client** | The connect app (designed) | Windows → macOS |
| **Web dashboards** | Organization dashboard + Platform console (designed) | Browser |
| **Backend** | Control plane (API, auth, tenancy) + data plane (signaling, relays) | Linux servers |

---

## 2. Technology Stack (cross-platform is a day-1 decision)

The single most important rule: **the agent core must be written against platform abstractions from the first commit**, even though we ship Windows first. Retrofitting macOS into a Win32-coupled codebase is a rewrite.

### 2.1 Agent core — Rust

Rust is the right choice: memory-safe (this is security software), single binary, first-class on both OSes, and it opens the option of building on **RustDesk** (open-source, battle-tested screen streaming; commercial license available) instead of writing capture/encode/transport from scratch.

**Decision to make in Phase 0:** fork RustDesk vs. build custom core. Recommendation: **fork RustDesk**, strip its UI, keep the protocol/capture/relay layers, and wrap them in our own agent with our enrollment, policy, and monitoring modules. Saves an estimated 9–12 engineer-months and inherits years of NAT-traversal edge-case fixes. Budget for a commercial license to escape AGPL obligations for the proprietary parts.

**Platform abstraction layer (the macOS insurance policy).** Define traits/interfaces for the five things that differ per OS, with Windows and macOS implementations behind them:

| Capability | Windows implementation | macOS implementation (later) |
|---|---|---|
| Screen capture | DXGI Desktop Duplication | ScreenCaptureKit |
| Video encode | NVENC / QuickSync / x264 fallback | VideoToolbox (hardware H.264/HEVC) |
| Input injection | SendInput | CGEvent (requires Accessibility permission) |
| Service/daemon | Windows Service | launchd LaunchDaemon + LaunchAgent pair |
| System info & sensors | WMI, LibreHardwareMonitor-style | IOKit / SMC |

Everything above this layer — protocol, session logic, enrollment, policy engine, monitoring scheduler — is shared code and never touches an OS API directly.

### 2.2 Desktop client — Tauri

**Tauri** (Rust backend + system webview): our client UI is already designed in HTML/CSS, and Tauri lets us ship it nearly as-is with a Rust core underneath. ~10 MB installers, native performance where it matters, one codebase for Windows and macOS. Electron is the fallback if we hit webview inconsistencies, at the cost of 150 MB installers. The in-session viewer window (video rendering) is native Rust/wgpu inside the Tauri shell for latency reasons — do not render the remote video stream through the DOM.

### 2.3 Backend

| Layer | Choice | Notes |
|---|---|---|
| API / control plane | **Go** | Single static binary — critical for the self-hosted installer; excellent concurrency for signaling |
| Database | **PostgreSQL 16** | Row-level security keyed on `organization_id`; one schema serves cloud and self-hosted |
| Presence & session state | **Redis** | Which agents are online, session brokering |
| Signaling | Go, WebSocket | Agents hold one persistent connection; ~50k connections per node |
| Relay | From RustDesk (Rust) | Stateless byte-forwarder; deploy per region on Hetzner/OVH bare metal |
| Web dashboards | **React + TypeScript (Next.js)** | Translate the approved HTML designs into a component library first |
| Packaging | **Docker Compose** for self-hosted; Kubernetes optional for our cloud | One `docker compose up` must bring up a full self-hosted stack |

### 2.4 Protocol & security

WebRTC-style connectivity: ICE/STUN for P2P hole-punching, TURN-equivalent relay fallback. DTLS 1.3 end-to-end encryption with keys negotiated between endpoints — relays forward ciphertext only. Signed agent binaries and signed update manifests. Certificate pinning between agent and control plane.

---

## 3. macOS Readiness Checklist (do these early, they have long lead times)

1. **Apple Developer account + Developer ID certificates** — enroll in Phase 0 (company verification can take weeks).
2. **Code signing & notarization pipeline** — every macOS build must be signed and notarized or Gatekeeper blocks it. Build this into CI before the first macOS beta, not after.
3. **TCC permissions UX** — macOS requires the *user* to grant Screen Recording and Accessibility permissions in System Settings; they cannot be granted programmatically. Design the guided first-run flow (with screenshots/deep links) as a real feature. For MDM-managed fleets, support PPPC profiles that pre-approve permissions — this is how unattended access on macOS actually works at scale, and our simulator clients with Mac hardware will need it.
4. **launchd architecture** — a root LaunchDaemon for the service + a per-user LaunchAgent for the screen session, talking over XPC. Different from the single Windows service; design the agent's process model to accommodate both.
5. **Universal binaries** — build for Apple Silicon + Intel from the start (Rust makes this cheap).
6. **No kernel extensions** — everything needed is available in user space on macOS 13+; avoid anything that smells like a kext.

---

## 4. Team

Minimum viable team for the timeline below (7 people):

| Role | Count | Focus |
|---|---|---|
| Rust engineer (agent/protocol) | 2 | RustDesk fork, platform abstraction, capture/encode, later macOS port |
| Backend engineer (Go) | 2 | Control plane, tenancy, signaling, self-hosted packaging |
| Frontend engineer (React/Tauri) | 1–2 | Dashboards + desktop client from approved designs |
| DevOps/SRE | 1 | CI/CD, relay fleet, monitoring, release signing |
| QA (from Phase 1 beta) | 1 | Test lab, cross-version Windows matrix, later macOS |

Product/design: you + contract designer as needed (design language is established). Security audit: external firm, contracted twice (see §7).

---

## 5. Phased Timeline

### Phase 0 — Foundation (Weeks 1–8)
- RustDesk evaluation spike: fork it, connect two machines through our own relay, measure latency/quality. **Go/no-go decision on fork vs. custom by week 4.**
- Control plane skeleton: Postgres schema with org/group/device/user/role model, RLS enforced; auth (OIDC + 2FA); agent enrollment with pre-baked tokens.
- Platform abstraction layer defined in code (traits + Windows impls, macOS stubs).
- CI/CD: signed Windows builds, versioned update manifests; Apple Developer enrollment started.
- **Exit criteria:** one Windows PC enrolled into an org via token, visible online in a rough dashboard, controllable through our relay.

### Phase 1 — MVP (Months 3–7)
- Full remote control: multi-monitor, clipboard, file transfer, session passwords, unattended + attended modes, P2P with relay fallback, adaptive quality.
- Desktop client (Tauri) built from the approved design — all seven tabs.
- Organization dashboard: device grid, groups, users & roles with the full permission matrix, session history, audit log.
- Platform console: organizations, licensing, relay health.
- Relay deployment: EU-Central on bare metal; P2P success telemetry from day 1.
- Security: E2E encryption, audit logging, per-device passwords, session consent prompts.
- **Milestone M1 (month 5): internal dogfood** — your own team uses Hangar exclusively to support 2–3 friendly client sites.
- **Milestone M2 (month 7): closed beta** with LOT Flight Academy-type client on their real fleet.

### Phase 2 — Monitoring & Management + macOS (Months 8–12)
- Monitoring: CPU/RAM/disk/temperature collection, alert policies per group, notification channels (email, webhook).
- Remote terminal, task manager, file explorer, event log viewer, Wake-on-LAN.
- Session recording with org-level retention policies.
- Software & hardware inventory.
- Per-organization branding (rebrandable agent/client).
- **macOS agent + client port** (months 9–12): implement the macOS side of the abstraction layer, TCC onboarding flow, notarized universal builds, MDM/PPPC support. Beta on your own Macs at month 11.
- **Self-hosted GA**: Docker Compose package, offline license files, signed update channel, install docs. First self-hosted pilot client.
- **Milestone M3 (month 10): commercial launch, cloud, Windows.**
- **Milestone M4 (month 12): macOS beta + self-hosted GA.**

### Phase 3 — Fleet Automation (Months 13–18)
- Configuration profiles: wallpaper, naming, required software, service watchdogs; drift detection and compliance reports.
- Scripting engine (PowerShell / bash-zsh on macOS) with library, scheduling, group targeting.
- Software deployment and patch management with maintenance windows.
- Public API + webhooks.
- Backup module (or integration) — decide build vs. partner by month 13.
- Regional relay expansion driven by client geography; self-hosted relay option for large clients.
- **Milestone M5 (month 15): macOS GA.**
- **Milestone M6 (month 18): automation suite GA** — the full "we keep your simulators running" offering.

---

## 6. Testing & QA

- **Hardware lab:** minimum 6 Windows boxes covering Win 10 21H2 → Win 11 latest, NVIDIA + Intel + AMD GPUs, one multi-GPU/multi-monitor rig mirroring a real sim station; 2 Macs (Apple Silicon + Intel) from Phase 2.
- **Network torture rig:** NAT-type matrix (full-cone → symmetric), packet loss/latency injection (tc/netem), double-NAT and CGNAT cases — P2P success rate is a tested metric with a target (≥70%), not an aspiration.
- **Automated:** protocol fuzzing, permission-matrix tests (every role × every endpoint), soak tests (agent running 30 days without leak/disconnect), update/rollback tests.
- **Release trains:** stable + beta channels; self-hosted clients pinned to stable with manual approval.

## 7. Security Program

- External penetration test before commercial launch (M3) and again before self-hosted GA (M4) — self-hosted means shipping your server code into hostile analysis environments; assume it will be reverse-engineered.
- Threat model workshop in Phase 0 (asset: unattended admin access to client machines — the crown jewels).
- Signed everything: agents, updates, license files. Key ceremony and offline signing keys.
- Vulnerability disclosure policy + rapid-patch path (target: critical fix shipped < 72 h).
- SOC 2 Type I preparation from Phase 2 if targeting defence/enterprise; log architecture designed for it from Phase 0.

## 8. Infrastructure & DevOps

- Cloud control plane: managed Postgres + 2 app nodes (any major cloud), ~€300–500/mo initially.
- Relays: Hetzner/OVH dedicated, 1 Gbps unmetered, EU first (€60–100/mo each), added per region on demand.
- Observability: Prometheus/Grafana + Sentry; per-relay dashboards (load, bandwidth, session count, P2P ratio) — these feed the Platform console UI directly.
- Release infrastructure: build farm with Windows + macOS runners, artifact signing service, update CDN.

## 9. Budget Envelope (18 months, rough)

| Item | Estimate |
|---|---|
| Team (7 people, blended EU rates) | €1.1–1.5 M |
| RustDesk commercial license / SDK alternatives | €10–40 k |
| Infrastructure (18 mo) | €15–25 k |
| Security audits (×2) + code signing certs | €40–70 k |
| Hardware lab | €15–20 k |
| **Total** | **≈ €1.2–1.7 M** |

Lean variant: 4–5 people, Phase 3 pushed to month 24, ≈ €700 k–1 M. The fork-vs-build decision in Phase 0 is the biggest lever on this number.

## 10. Top Risks & Mitigations

1. **Underestimating the streaming core** → mitigated by the RustDesk fork decision gate in week 4; if the fork fails evaluation, license a commercial SDK before considering building from scratch.
2. **macOS unattended access friction (TCC)** → mitigated by MDM/PPPC support and a polished guided-permission flow; set client expectations that macOS unattended requires one-time on-device setup.
3. **Relay bandwidth costs eroding margin** → P2P telemetry + alerting on P2P ratio; bare-metal relays only; self-hosted relay for heavy clients.
4. **Self-hosted piracy/reverse engineering** → signed license files with expiry, feature flags server-side, legal terms; accept some leakage as cost of the segment.
5. **Windows/macOS behavioral drift** → the abstraction layer is enforced in code review: no OS API calls above it, ever.
6. **Single-client dependency in beta** → recruit 3 beta orgs minimum before M3, including one non-aviation to validate the general RMM story.

## 11. Definition of Done per Milestone

- **M1:** Your support team abandons TeamViewer internally.
- **M2:** A real client's technicians run daily operations in Hangar for 30 days; NPS-style check ≥ 8/10.
- **M3:** First paying cloud org; billing live; pen test findings (critical/high) closed.
- **M4:** One self-hosted install completed by the client's own IT using only the docs; macOS agent controls a Mac in the lab.
- **M5:** macOS agent at feature parity for remote control + monitoring; notarized, MDM-deployable.
- **M6:** One client fleet fully managed by policies (config profile + patching + alerting) with zero manual setup on new devices.

---

## 12. Design Implementation Reference (for the build agent / dev team)

The three appendices below contain the **full, approved HTML/CSS/JS source** of the UI designs. They are the visual source of truth. Any agent or engineer implementing the product must treat them as the spec: extract the tokens, reproduce the components exactly, then wire them to real data.

### 12.1 Design tokens (extract into a shared package, use everywhere)

```css
:root{
  /* color */
  --ink:#0B0C15;          /* primary dark / buttons / dark surfaces */
  --paper:#F5F6F9;        /* app background */
  --card:#FFFFFF;         /* card surface */
  --line:#E8E9F0;         /* borders, dividers */
  --text:#0B0C15;
  --muted:#6E7185;        /* secondary text */
  --accent:#5B5BF5;       /* brand indigo */
  --accent-soft:#EEEEFE;  /* accent tint backgrounds */
  --grad:linear-gradient(135deg,#5B5BF5 0%,#9D5CFF 55%,#3EC8FF 120%); /* brand gradient */
  --ok:#1FC98B;  --warn:#FFAA1D;  --danger:#F5484D;
  /* radius */
  --r-lg:22px; --r-md:14px; --r-pill:999px;
}
```

Typography: **Schibsted Grotesk** (display, weights 700/800, letter-spacing −0.02em), **Inter** (UI/body, 400–600), **JetBrains Mono** (device IDs, IPs, versions, all machine data). Device IDs are always formatted `000 000 000` in mono.

### 12.2 Component inventory (build these once, reuse across all three surfaces)

| Component | Appears in | Key rules |
|---|---|---|
| ID card ("payment card") | Desktop client, marketing | Dark ink surface, gradient radial glows via `::before`, mono ID at 1.75–1.9rem, gold chip decoration |
| Device card | Org dashboard | Thumbnail area with status pill (top-left) + temp chip (top-right), mono ID, group tag, gradient Connect button |
| Status LED | Everywhere | 7–9px dot: `--ok` online, `--danger` offline, `#3EC8FF` in-session, `--warn` alert; glow (`box-shadow`) only on live/online |
| Pill badge | Everywhere | P2P (green tint), Relay (blue tint), group tag (accent tint), plan Cloud/Self-hosted (accent tint / solid ink) |
| Toggle | Desktop client, settings | 42–44px track, `--ok` when on, `aria-checked` synced |
| Nav (sidebar / icon rail / pill tabs) | Dashboards / desktop app | Active state: `--accent-soft` bg + `--accent` text (light) or `rgba(93,91,245,.22)` (dark sidebar) |
| List row (`.lrow`) | Recent, address book, transfers, notifications | 15px vertical padding, hairline top border, hover `#FAFAFE` |
| Hero (dark) | Org dashboard, platform KPIs | Ink surface + two radial gradient glows, big Schibsted numerals |

Interaction conventions already encoded in the prototypes: hover lifts cards −2/−3px with deepened shadow; primary actions are pill-shaped; `prefers-reduced-motion` disables all animation; every interactive element has a visible `:focus-visible` ring in `--accent`.

### 12.3 Mapping prototypes → production

1. **Dashboards (Appendices A, B)** → React/Next.js. Convert each CSS block into the component library (tokens first), replace all hardcoded data (device lists, sessions, relay metrics, org table) with API queries. The layout grid, spacing, and states must match the prototypes pixel-close.
2. **Desktop client (Appendix C)** → Tauri. The HTML runs in the webview nearly unchanged; replace the demo `<script>` logic (fake connect flow, fake transfer progress, clipboard copy of hardcoded secret) with Tauri `invoke` calls into the Rust core. The seven views and their switching logic are the intended information architecture — keep them.
3. **What is demo-only and must be replaced:** pravatar.cc avatar URLs; the hardcoded one-time password `k4v9m2`; all device/session/transfer fixtures; `setTimeout`-based connect simulation; `setInterval` transfer progress. Everything else (markup structure, classes, tokens, states) is production-intended.
4. **Accessibility floor:** preserve the existing `role`/`aria-*` attributes (tablist/tab/tabpanel, switch, alert) and extend the same pattern to new components.

### 12.4 Appendix index

- **Appendix A** — Organization dashboard (client view): `organization-dashboard.html`
- **Appendix B** — Platform console (admin view): `platform-admin.html`
- **Appendix C** — Desktop client, full layout with all functional tabs: `desktop-app-full.html`

---

## Appendix A — Organization Dashboard (full source)

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Hangar — LOT Flight Academy</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Schibsted+Grotesk:wght@500;700;800&family=Inter:wght@400;500;600&family=JetBrains+Mono:wght@500;600&display=swap" rel="stylesheet">
<style>
:root{
  --ink:#0B0C15;
  --ink-soft:#1A1C2B;
  --paper:#F5F6F9;
  --card:#FFFFFF;
  --line:#E8E9F0;
  --text:#0B0C15;
  --muted:#6E7185;
  --accent:#5B5BF5;
  --accent-soft:#EEEEFE;
  --grad:linear-gradient(135deg,#5B5BF5 0%,#9D5CFF 55%,#3EC8FF 120%);
  --ok:#1FC98B;
  --warn:#FFAA1D;
  --danger:#F5484D;
  --r-lg:24px;
  --r-md:16px;
  --r-pill:999px;
  --shadow:0 1px 2px rgba(11,12,21,.04),0 8px 24px rgba(11,12,21,.06);
}
*{margin:0;padding:0;box-sizing:border-box}
html{font-size:15px}
body{
  font-family:'Inter',system-ui,sans-serif;
  background:var(--paper);
  color:var(--text);
  -webkit-font-smoothing:antialiased;
  display:grid;
  grid-template-columns:248px 1fr;
  min-height:100vh;
}
h1,h2,h3,.display{font-family:'Schibsted Grotesk',sans-serif}
.mono{font-family:'JetBrains Mono',monospace}
button{font-family:inherit;cursor:pointer;border:none;background:none}
a{text-decoration:none;color:inherit}
:focus-visible{outline:2px solid var(--accent);outline-offset:2px;border-radius:6px}

/* ============ SIDEBAR ============ */
.sidebar{
  background:var(--card);
  border-right:1px solid var(--line);
  padding:24px 16px;
  display:flex;flex-direction:column;gap:8px;
  position:sticky;top:0;height:100vh;
}
.brand{display:flex;align-items:center;gap:10px;padding:4px 10px 20px}
.brand-mark{
  width:34px;height:34px;border-radius:11px;
  background:var(--grad);
  display:grid;place-items:center;flex:none;
}
.brand-mark svg{width:18px;height:18px}
.brand-name{font-family:'Schibsted Grotesk';font-weight:800;font-size:1.25rem;letter-spacing:-.02em}
.org-chip{
  margin:0 6px 18px;padding:10px 12px;
  background:var(--paper);border-radius:var(--r-md);
  display:flex;align-items:center;gap:10px;
}
.org-avatar{
  width:30px;height:30px;border-radius:9px;flex:none;
  background:var(--ink);color:#fff;
  display:grid;place-items:center;
  font-family:'Schibsted Grotesk';font-weight:700;font-size:.75rem;
}
.org-chip .org-name{font-weight:600;font-size:.85rem;line-height:1.2}
.org-chip .org-plan{font-size:.7rem;color:var(--muted)}
.nav-label{font-size:.68rem;font-weight:600;letter-spacing:.09em;text-transform:uppercase;color:var(--muted);padding:14px 12px 6px}
.nav-item{
  display:flex;align-items:center;gap:11px;
  padding:10px 12px;border-radius:12px;
  font-weight:500;font-size:.9rem;color:var(--muted);
  transition:background .15s,color .15s;
}
.nav-item svg{width:19px;height:19px;stroke:currentColor;fill:none;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round;flex:none}
.nav-item:hover{background:var(--paper);color:var(--text)}
.nav-item.active{background:var(--accent-soft);color:var(--accent);font-weight:600}
.nav-badge{margin-left:auto;background:var(--danger);color:#fff;font-size:.68rem;font-weight:600;padding:2px 7px;border-radius:var(--r-pill)}
.sidebar-foot{margin-top:auto;padding:12px;border-top:1px solid var(--line);display:flex;align-items:center;gap:10px}
.sidebar-foot img{width:32px;height:32px;border-radius:50%}
.user-name{font-weight:600;font-size:.85rem}
.user-role{font-size:.72rem;color:var(--muted)}

/* ============ MAIN ============ */
.main{padding:24px 32px 48px;max-width:1240px;width:100%}
.topbar{display:flex;align-items:center;gap:16px;margin-bottom:24px}
.search{
  flex:1;max-width:420px;display:flex;align-items:center;gap:10px;
  background:var(--card);border:1px solid var(--line);border-radius:var(--r-pill);
  padding:10px 16px;color:var(--muted);font-size:.88rem;
}
.search svg{width:16px;height:16px;stroke:currentColor;fill:none;stroke-width:2;stroke-linecap:round}
.search input{border:none;outline:none;background:none;font-family:inherit;font-size:.88rem;width:100%;color:var(--text)}
.topbar-spacer{flex:1}
.icon-btn{
  width:42px;height:42px;border-radius:50%;
  background:var(--card);border:1px solid var(--line);
  display:grid;place-items:center;color:var(--text);position:relative;
  transition:background .15s;
}
.icon-btn:hover{background:var(--paper)}
.icon-btn svg{width:18px;height:18px;stroke:currentColor;fill:none;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
.icon-btn .dot{position:absolute;top:9px;right:10px;width:8px;height:8px;border-radius:50%;background:var(--danger);border:2px solid var(--card)}
.btn-primary{
  display:inline-flex;align-items:center;gap:8px;
  background:var(--ink);color:#fff;
  padding:11px 20px;border-radius:var(--r-pill);
  font-weight:600;font-size:.88rem;
  transition:transform .15s,box-shadow .15s;
}
.btn-primary:hover{transform:translateY(-1px);box-shadow:0 8px 20px rgba(11,12,21,.2)}
.btn-primary svg{width:16px;height:16px;stroke:currentColor;fill:none;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}

/* ============ HERO ============ */
.hero{
  background:var(--ink);color:#fff;
  border-radius:var(--r-lg);
  padding:28px 32px;
  position:relative;overflow:hidden;
  margin-bottom:20px;
}
.hero::before{
  content:"";position:absolute;inset:0;
  background:
    radial-gradient(560px 320px at 88% -20%,rgba(93,91,245,.55),transparent 62%),
    radial-gradient(420px 260px at 70% 130%,rgba(62,200,255,.28),transparent 60%);
  pointer-events:none;
}
.hero-inner{position:relative;display:flex;align-items:flex-end;justify-content:space-between;gap:24px;flex-wrap:wrap}
.hero h1{font-size:1.7rem;font-weight:800;letter-spacing:-.02em;margin-bottom:6px}
.hero p{color:rgba(255,255,255,.62);font-size:.9rem;max-width:420px}
.hero-stats{display:flex;gap:36px;flex-wrap:wrap}
.hstat .v{font-family:'Schibsted Grotesk';font-weight:800;font-size:2rem;letter-spacing:-.02em;line-height:1.1;display:flex;align-items:baseline;gap:8px}
.hstat .v .up{font-size:.75rem;font-weight:600;color:#5CE6B8}
.hstat .l{font-size:.76rem;color:rgba(255,255,255,.55);margin-top:4px;display:flex;align-items:center;gap:6px}
.hstat .l .led{width:7px;height:7px;border-radius:50%}
.led.g{background:var(--ok);box-shadow:0 0 8px rgba(31,201,139,.8)}
.led.r{background:var(--danger)}
.led.b{background:#3EC8FF}
.led.y{background:var(--warn)}

/* ============ ALERT STRIP ============ */
.alert-strip{
  display:flex;align-items:center;gap:14px;
  background:#FFF7E8;border:1px solid #FFE3AE;border-radius:var(--r-md);
  padding:13px 18px;margin-bottom:24px;font-size:.87rem;
}
.alert-strip .ic{
  width:32px;height:32px;border-radius:10px;background:var(--warn);color:#fff;
  display:grid;place-items:center;flex:none;
}
.alert-strip .ic svg{width:16px;height:16px;stroke:currentColor;fill:none;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}
.alert-strip b{font-weight:600}
.alert-strip .when{color:var(--muted);margin-left:auto;font-size:.78rem;white-space:nowrap}
.alert-strip a{color:var(--accent);font-weight:600;white-space:nowrap}

/* ============ SECTION HEADS ============ */
.sec-head{display:flex;align-items:center;justify-content:space-between;margin:26px 0 14px}
.sec-head h2{font-size:1.15rem;font-weight:700;letter-spacing:-.01em}
.filters{display:flex;gap:8px}
.chip{
  padding:7px 14px;border-radius:var(--r-pill);
  font-size:.8rem;font-weight:500;color:var(--muted);
  background:var(--card);border:1px solid var(--line);
  transition:all .15s;
}
.chip:hover{color:var(--text)}
.chip.on{background:var(--ink);color:#fff;border-color:var(--ink)}

/* ============ DEVICE GRID — signature cards ============ */
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(272px,1fr));gap:16px}
.dev{
  background:var(--card);border-radius:var(--r-lg);
  box-shadow:var(--shadow);overflow:hidden;
  transition:transform .18s,box-shadow .18s;
  display:flex;flex-direction:column;
}
.dev:hover{transform:translateY(-3px);box-shadow:0 4px 8px rgba(11,12,21,.06),0 20px 40px rgba(11,12,21,.12)}
.dev-screen{
  height:128px;position:relative;
  background:linear-gradient(160deg,#171930,#0B0C15 70%);
  overflow:hidden;
}
.dev-screen::after{
  content:"";position:absolute;inset:0;
  background:linear-gradient(180deg,transparent 40%,rgba(11,12,21,.55));
}
.scanline{position:absolute;inset:0;opacity:.5;
  background:repeating-linear-gradient(0deg,transparent 0 3px,rgba(255,255,255,.02) 3px 4px)}
.dev-screen .fake{position:absolute;border-radius:4px;background:rgba(255,255,255,.07)}
.dev-screen.warn-glow{background:linear-gradient(160deg,#2E1B10,#0B0C15 75%)}
.dev-screen.offline-bg{background:linear-gradient(160deg,#1E1F26,#101116 75%)}
.st{
  position:absolute;top:12px;left:12px;z-index:2;
  display:flex;align-items:center;gap:6px;
  background:rgba(11,12,21,.55);backdrop-filter:blur(6px);
  border:1px solid rgba(255,255,255,.12);
  color:#fff;font-size:.68rem;font-weight:600;
  padding:4px 10px;border-radius:var(--r-pill);
}
.temp{
  position:absolute;top:12px;right:12px;z-index:2;
  font-family:'JetBrains Mono';font-size:.68rem;font-weight:600;
  background:rgba(11,12,21,.55);backdrop-filter:blur(6px);
  border:1px solid rgba(255,255,255,.12);color:#B9F5DE;
  padding:4px 9px;border-radius:var(--r-pill);
}
.temp.hot{color:#FFD79A;border-color:rgba(255,170,29,.5)}
.dev-body{padding:16px 18px 18px;display:flex;flex-direction:column;gap:4px;flex:1}
.dev-name{font-weight:600;font-size:.95rem;display:flex;align-items:center;gap:8px}
.tag{
  font-size:.66rem;font-weight:600;color:var(--accent);
  background:var(--accent-soft);padding:3px 8px;border-radius:var(--r-pill);
}
.dev-id{
  font-family:'JetBrains Mono';font-weight:600;font-size:1.05rem;
  letter-spacing:.06em;margin-top:6px;
}
.dev-meta{font-size:.75rem;color:var(--muted);display:flex;gap:12px;margin-top:2px}
.dev-actions{display:flex;gap:8px;margin-top:14px}
.btn-connect{
  flex:1;padding:9px 0;border-radius:var(--r-pill);
  background:var(--grad);color:#fff;font-weight:600;font-size:.82rem;
  transition:opacity .15s,transform .15s;
}
.btn-connect:hover{opacity:.92;transform:translateY(-1px)}
.btn-connect:disabled{background:var(--line);color:var(--muted);cursor:not-allowed;transform:none}
.btn-ghost{
  width:38px;border-radius:var(--r-pill);
  border:1px solid var(--line);display:grid;place-items:center;color:var(--muted);
  transition:all .15s;
}
.btn-ghost:hover{color:var(--text);border-color:var(--muted)}
.btn-ghost svg{width:15px;height:15px;stroke:currentColor;fill:none;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}

/* ============ TWO-COL LOWER ============ */
.lower{display:grid;grid-template-columns:1.6fr 1fr;gap:16px;margin-top:32px}
.panel{background:var(--card);border-radius:var(--r-lg);box-shadow:var(--shadow);padding:22px 24px}
.panel h3{font-size:1rem;font-weight:700;margin-bottom:16px;display:flex;align-items:center;justify-content:space-between}
.panel h3 a{font-size:.78rem;color:var(--accent);font-weight:600}
.sess{display:flex;align-items:center;gap:13px;padding:11px 0;border-top:1px solid var(--line);font-size:.85rem}
.sess:first-of-type{border-top:none}
.sess .who{
  width:34px;height:34px;border-radius:50%;flex:none;
  background:var(--accent-soft);color:var(--accent);
  display:grid;place-items:center;font-weight:700;font-size:.72rem;
}
.sess .what b{font-weight:600}
.sess .what span{color:var(--muted);font-size:.76rem;display:block;margin-top:1px}
.sess .mode{
  margin-left:auto;font-size:.68rem;font-weight:600;
  padding:4px 10px;border-radius:var(--r-pill);white-space:nowrap;
}
.mode.p2p{background:#E5FAF1;color:#0E9B69}
.mode.relay{background:#EAF4FF;color:#2276D2}
.mode.live{background:var(--ink);color:#fff;display:inline-flex;align-items:center;gap:5px}
.mode.live .led{width:6px;height:6px;border-radius:50%;background:var(--ok);animation:pulse 1.4s infinite}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.35}}
.usage-row{display:flex;align-items:center;gap:12px;padding:10px 0;border-top:1px solid var(--line);font-size:.83rem}
.usage-row:first-of-type{border-top:none}
.usage-row .g-name{width:118px;font-weight:600;flex:none}
.bar{flex:1;height:8px;border-radius:var(--r-pill);background:var(--paper);overflow:hidden}
.bar i{display:block;height:100%;border-radius:var(--r-pill);background:var(--grad)}
.usage-row .hrs{font-family:'JetBrains Mono';font-size:.75rem;color:var(--muted);width:56px;text-align:right}

@media(max-width:1080px){.lower{grid-template-columns:1fr}}
@media(max-width:860px){
  body{grid-template-columns:1fr}
  .sidebar{display:none}
  .main{padding:16px}
  .hero-inner{flex-direction:column;align-items:flex-start}
}
@media(prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
</style>
</head>
<body>

<!-- ================= SIDEBAR ================= -->
<aside class="sidebar">
  <div class="brand">
    <div class="brand-mark">
      <svg viewBox="0 0 24 24" fill="none"><path d="M3 18 L12 4 L21 18 M7.5 18 L12 11 L16.5 18" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>
    </div>
    <span class="brand-name">Hangar</span>
  </div>

  <div class="org-chip">
    <div class="org-avatar">LT</div>
    <div>
      <div class="org-name">LOT Flight Academy</div>
      <div class="org-plan">Cloud · 14 devices</div>
    </div>
  </div>

  <div class="nav-label">Fleet</div>
  <a class="nav-item active" href="#">
    <svg viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="9" rx="2"/><rect x="14" y="3" width="7" height="5" rx="2"/><rect x="14" y="12" width="7" height="9" rx="2"/><rect x="3" y="16" width="7" height="5" rx="2"/></svg>
    Dashboard
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><rect x="2" y="4" width="20" height="13" rx="2"/><path d="M8 21h8M12 17v4"/></svg>
    Devices
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><rect x="3" y="3" width="8" height="8" rx="2"/><rect x="13" y="13" width="8" height="8" rx="2"/><rect x="13" y="3" width="8" height="8" rx="2"/><rect x="3" y="13" width="8" height="8" rx="2"/></svg>
    Groups
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><path d="M17 3a2.8 2.8 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/></svg>
    Sessions
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0"/></svg>
    Alerts
    <span class="nav-badge">3</span>
  </a>

  <div class="nav-label">Organization</div>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75"/></svg>
    Users &amp; roles
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"/></svg>
    Policies
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h0a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51h0a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v0a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z"/></svg>
    Settings
  </a>

  <div class="sidebar-foot">
    <img src="https://i.pravatar.cc/64?img=12" alt="">
    <div>
      <div class="user-name">Marek Kowalski</div>
      <div class="user-role">Org Admin</div>
    </div>
  </div>
</aside>

<!-- ================= MAIN ================= -->
<main class="main">

  <div class="topbar">
    <label class="search">
      <svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/></svg>
      <input placeholder="Search devices, users, sessions…" aria-label="Search">
    </label>
    <div class="topbar-spacer"></div>
    <button class="icon-btn" aria-label="Notifications">
      <svg viewBox="0 0 24 24"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0"/></svg>
      <span class="dot"></span>
    </button>
    <button class="btn-primary">
      <svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg>
      Connect to device
    </button>
  </div>

  <!-- HERO -->
  <section class="hero">
    <div class="hero-inner">
      <div>
        <h1>Good morning, Marek</h1>
        <p>Your simulator fleet across Warsaw and Rzeszów. One device needs attention before today's 14:00 session block.</p>
      </div>
      <div class="hero-stats">
        <div class="hstat">
          <div class="v">12</div>
          <div class="l"><span class="led g"></span>Devices online</div>
        </div>
        <div class="hstat">
          <div class="v">2</div>
          <div class="l"><span class="led r"></span>Offline</div>
        </div>
        <div class="hstat">
          <div class="v">3</div>
          <div class="l"><span class="led b"></span>Active sessions</div>
        </div>
        <div class="hstat">
          <div class="v">248<span class="up">↑ 12% wk</span></div>
          <div class="l"><span class="led y"></span>Sim hours this month</div>
        </div>
      </div>
    </div>
  </section>

  <!-- ALERT -->
  <div class="alert-strip" role="alert">
    <div class="ic"><svg viewBox="0 0 24 24"><path d="M12 9v4M12 17h.01M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/></svg></div>
    <div><b>ACL-CAN · GPU 84 °C</b> — above the 80 °C policy limit for FNPT Sims. Fan speed at 100% for 22 min.</div>
    <span class="when">8 min ago</span>
    <a href="#">Open device →</a>
  </div>

  <!-- DEVICES -->
  <div class="sec-head">
    <h2>Devices</h2>
    <div class="filters">
      <button class="chip on">All · 14</button>
      <button class="chip">FNPT Sims · 6</button>
      <button class="chip">FTD Bay · 4</button>
      <button class="chip">Briefing Rooms · 4</button>
    </div>
  </div>

  <div class="grid">

    <!-- card 1: warning -->
    <article class="dev">
      <div class="dev-screen warn-glow">
        <div class="scanline"></div>
        <div class="fake" style="left:14px;top:18px;width:52%;height:10px"></div>
        <div class="fake" style="left:14px;top:36px;width:70%;height:44px"></div>
        <div class="fake" style="left:14px;top:88px;width:38%;height:10px"></div>
        <span class="st"><span class="led g"></span>Online</span>
        <span class="temp hot">GPU 84°</span>
      </div>
      <div class="dev-body">
        <div class="dev-name">ACL-CAN <span class="tag">FNPT Sims</span></div>
        <div class="dev-id">722 059 036</div>
        <div class="dev-meta"><span>89.171.135.73</span><span>Win 10 · 19044</span></div>
        <div class="dev-actions">
          <button class="btn-connect">Connect</button>
          <button class="btn-ghost" aria-label="Terminal"><svg viewBox="0 0 24 24"><path d="m4 17 6-6-6-6M12 19h8"/></svg></button>
          <button class="btn-ghost" aria-label="More"><svg viewBox="0 0 24 24"><circle cx="12" cy="5" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="12" cy="19" r="1"/></svg></button>
        </div>
      </div>
    </article>

    <!-- card 2 -->
    <article class="dev">
      <div class="dev-screen">
        <div class="scanline"></div>
        <div class="fake" style="left:14px;top:16px;width:44%;height:52px"></div>
        <div class="fake" style="left:58%;top:16px;width:30%;height:52px"></div>
        <div class="fake" style="left:14px;top:80px;width:74%;height:12px"></div>
        <span class="st"><span class="led g"></span>Online</span>
        <span class="temp">GPU 61°</span>
      </div>
      <div class="dev-body">
        <div class="dev-name">MDS-01 <span class="tag">FNPT Sims</span></div>
        <div class="dev-id">688 630 782</div>
        <div class="dev-meta"><span>89.171.135.73</span><span>Win 11 · 22631</span></div>
        <div class="dev-actions">
          <button class="btn-connect">Connect</button>
          <button class="btn-ghost" aria-label="Terminal"><svg viewBox="0 0 24 24"><path d="m4 17 6-6-6-6M12 19h8"/></svg></button>
          <button class="btn-ghost" aria-label="More"><svg viewBox="0 0 24 24"><circle cx="12" cy="5" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="12" cy="19" r="1"/></svg></button>
        </div>
      </div>
    </article>

    <!-- card 3: in session -->
    <article class="dev">
      <div class="dev-screen">
        <div class="scanline"></div>
        <div class="fake" style="left:14px;top:14px;width:76%;height:64px"></div>
        <div class="fake" style="left:14px;top:86px;width:52%;height:10px"></div>
        <span class="st"><span class="led b"></span>In session · A. Nowak</span>
        <span class="temp">GPU 58°</span>
      </div>
      <div class="dev-body">
        <div class="dev-name">DISPLAY-PC <span class="tag">FTD Bay</span></div>
        <div class="dev-id">421 250 992</div>
        <div class="dev-meta"><span>216.147.121.105</span><span>Win 10 · 19045</span></div>
        <div class="dev-actions">
          <button class="btn-connect">Join session</button>
          <button class="btn-ghost" aria-label="Terminal"><svg viewBox="0 0 24 24"><path d="m4 17 6-6-6-6M12 19h8"/></svg></button>
          <button class="btn-ghost" aria-label="More"><svg viewBox="0 0 24 24"><circle cx="12" cy="5" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="12" cy="19" r="1"/></svg></button>
        </div>
      </div>
    </article>

    <!-- card 4: offline -->
    <article class="dev">
      <div class="dev-screen offline-bg">
        <span class="st"><span class="led r"></span>Offline · 2 d</span>
      </div>
      <div class="dev-body">
        <div class="dev-name">HOST-5884 <span class="tag">Briefing Rooms</span></div>
        <div class="dev-id">167 674 074</div>
        <div class="dev-meta"><span>Last seen Jul 29</span><span>Win 10 · 19044</span></div>
        <div class="dev-actions">
          <button class="btn-connect" disabled>Wake on LAN</button>
          <button class="btn-ghost" aria-label="More"><svg viewBox="0 0 24 24"><circle cx="12" cy="5" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="12" cy="19" r="1"/></svg></button>
        </div>
      </div>
    </article>

  </div>

  <!-- LOWER PANELS -->
  <div class="lower">
    <section class="panel">
      <h3>Recent sessions <a href="#">View all →</a></h3>
      <div class="sess">
        <div class="who">AN</div>
        <div class="what"><b>Anna Nowak</b> → DISPLAY-PC<span>Remote control · started 10:42</span></div>
        <span class="mode live"><span class="led"></span>Live · 18 min</span>
      </div>
      <div class="sess">
        <div class="who">MK</div>
        <div class="what"><b>Marek Kowalski</b> → ACL-CAN<span>File transfer · scenery update 2.4 GB</span></div>
        <span class="mode p2p">P2P · 09:15</span>
      </div>
      <div class="sess">
        <div class="who">JW</div>
        <div class="what"><b>Jan Wiśniewski</b> → MDS-01<span>Terminal · license check</span></div>
        <span class="mode relay">Relay · 08:03</span>
      </div>
      <div class="sess">
        <div class="who">HQ</div>
        <div class="what"><b>Hangar Support</b> → WS-25-008<span>Granted access · expires in 6 d</span></div>
        <span class="mode relay">Relay · Yesterday</span>
      </div>
    </section>

    <section class="panel">
      <h3>Sim hours by group <a href="#">July →</a></h3>
      <div class="usage-row"><span class="g-name">FNPT Sims</span><div class="bar"><i style="width:86%"></i></div><span class="hrs">142 h</span></div>
      <div class="usage-row"><span class="g-name">FTD Bay</span><div class="bar"><i style="width:52%"></i></div><span class="hrs">78 h</span></div>
      <div class="usage-row"><span class="g-name">Briefing Rooms</span><div class="bar"><i style="width:19%"></i></div><span class="hrs">28 h</span></div>
    </section>
  </div>

</main>
</body>
</html>
```

---

## Appendix B — Platform Console (full source)

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Hangar HQ — Platform Console</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Schibsted+Grotesk:wght@500;700;800&family=Inter:wght@400;500;600&family=JetBrains+Mono:wght@500;600&display=swap" rel="stylesheet">
<style>
:root{
  --ink:#0B0C15;
  --ink-2:#141623;
  --ink-3:#1E2133;
  --paper:#F5F6F9;
  --card:#FFFFFF;
  --line:#E8E9F0;
  --line-dark:rgba(255,255,255,.08);
  --text:#0B0C15;
  --muted:#6E7185;
  --muted-dark:rgba(255,255,255,.55);
  --accent:#5B5BF5;
  --accent-soft:#EEEEFE;
  --grad:linear-gradient(135deg,#5B5BF5 0%,#9D5CFF 55%,#3EC8FF 120%);
  --ok:#1FC98B;
  --warn:#FFAA1D;
  --danger:#F5484D;
  --r-lg:24px;
  --r-md:16px;
  --r-pill:999px;
  --shadow:0 1px 2px rgba(11,12,21,.04),0 8px 24px rgba(11,12,21,.06);
}
*{margin:0;padding:0;box-sizing:border-box}
html{font-size:15px}
body{
  font-family:'Inter',system-ui,sans-serif;
  background:var(--paper);color:var(--text);
  -webkit-font-smoothing:antialiased;
  display:grid;grid-template-columns:248px 1fr;min-height:100vh;
}
h1,h2,h3{font-family:'Schibsted Grotesk',sans-serif}
.mono{font-family:'JetBrains Mono',monospace}
button{font-family:inherit;cursor:pointer;border:none;background:none}
a{text-decoration:none;color:inherit}
:focus-visible{outline:2px solid #8B8BFF;outline-offset:2px;border-radius:6px}

/* ============ DARK SIDEBAR ============ */
.sidebar{
  background:var(--ink);color:#fff;
  padding:24px 16px;
  display:flex;flex-direction:column;gap:8px;
  position:sticky;top:0;height:100vh;
}
.brand{display:flex;align-items:center;gap:10px;padding:4px 10px 6px}
.brand-mark{width:34px;height:34px;border-radius:11px;background:var(--grad);display:grid;place-items:center;flex:none}
.brand-name{font-family:'Schibsted Grotesk';font-weight:800;font-size:1.25rem;letter-spacing:-.02em}
.hq-tag{
  margin:0 10px 18px;align-self:flex-start;
  font-family:'JetBrains Mono';font-size:.62rem;font-weight:600;letter-spacing:.14em;
  color:#9FE8FF;background:rgba(62,200,255,.12);
  border:1px solid rgba(62,200,255,.3);
  padding:3px 9px;border-radius:var(--r-pill);
}
.nav-label{font-size:.68rem;font-weight:600;letter-spacing:.09em;text-transform:uppercase;color:var(--muted-dark);padding:14px 12px 6px;opacity:.7}
.nav-item{
  display:flex;align-items:center;gap:11px;
  padding:10px 12px;border-radius:12px;
  font-weight:500;font-size:.9rem;color:var(--muted-dark);
  transition:background .15s,color .15s;
}
.nav-item svg{width:19px;height:19px;stroke:currentColor;fill:none;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round;flex:none}
.nav-item:hover{background:rgba(255,255,255,.06);color:#fff}
.nav-item.active{background:rgba(93,91,245,.22);color:#C9C9FF;font-weight:600}
.sidebar-foot{margin-top:auto;padding:12px;border-top:1px solid var(--line-dark);display:flex;align-items:center;gap:10px}
.sidebar-foot img{width:32px;height:32px;border-radius:50%}
.user-name{font-weight:600;font-size:.85rem}
.user-role{font-size:.72rem;color:var(--muted-dark)}

/* ============ MAIN ============ */
.main{padding:24px 32px 48px;max-width:1280px;width:100%}
.topbar{display:flex;align-items:center;gap:16px;margin-bottom:24px}
.page-title h1{font-size:1.45rem;font-weight:800;letter-spacing:-.02em}
.page-title p{font-size:.82rem;color:var(--muted);margin-top:2px}
.topbar-spacer{flex:1}
.btn-primary{
  display:inline-flex;align-items:center;gap:8px;
  background:var(--ink);color:#fff;
  padding:11px 20px;border-radius:var(--r-pill);
  font-weight:600;font-size:.88rem;
  transition:transform .15s,box-shadow .15s;
}
.btn-primary:hover{transform:translateY(-1px);box-shadow:0 8px 20px rgba(11,12,21,.2)}
.btn-secondary{
  display:inline-flex;align-items:center;gap:8px;
  background:var(--card);border:1px solid var(--line);color:var(--text);
  padding:11px 20px;border-radius:var(--r-pill);
  font-weight:600;font-size:.88rem;transition:background .15s;
}
.btn-secondary:hover{background:var(--paper)}

/* ============ KPI ROW ============ */
.kpis{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin-bottom:16px}
.kpi{
  background:var(--card);border-radius:var(--r-lg);
  box-shadow:var(--shadow);padding:20px 22px;position:relative;overflow:hidden;
}
.kpi.dark{background:var(--ink);color:#fff}
.kpi.dark::before{
  content:"";position:absolute;inset:0;
  background:radial-gradient(300px 180px at 90% -30%,rgba(93,91,245,.55),transparent 65%);
}
.kpi .l{font-size:.76rem;color:var(--muted);font-weight:500;position:relative}
.kpi.dark .l{color:var(--muted-dark)}
.kpi .v{font-family:'Schibsted Grotesk';font-weight:800;font-size:2rem;letter-spacing:-.02em;margin-top:6px;position:relative;display:flex;align-items:baseline;gap:8px}
.kpi .d{font-size:.72rem;font-weight:600;color:var(--ok)}
.kpi .d.down{color:var(--danger)}
.kpi .sub{font-size:.72rem;color:var(--muted);margin-top:4px;position:relative}
.kpi.dark .sub{color:var(--muted-dark)}

/* ============ RELAY REGIONS ============ */
.sec-head{display:flex;align-items:center;justify-content:space-between;margin:26px 0 14px}
.sec-head h2{font-size:1.15rem;font-weight:700;letter-spacing:-.01em}
.sec-head a{font-size:.8rem;color:var(--accent);font-weight:600}
.relays{display:grid;grid-template-columns:repeat(3,1fr);gap:14px}
.relay{
  background:var(--card);border-radius:var(--r-lg);box-shadow:var(--shadow);
  padding:20px 22px;
}
.relay-top{display:flex;align-items:center;gap:10px;margin-bottom:14px}
.flag{font-size:1.15rem}
.relay-top b{font-weight:700;font-size:.92rem}
.relay-top .host{font-family:'JetBrains Mono';font-size:.68rem;color:var(--muted);display:block;margin-top:1px}
.relay-top .pill{
  margin-left:auto;display:inline-flex;align-items:center;gap:5px;
  font-size:.68rem;font-weight:600;padding:4px 10px;border-radius:var(--r-pill);
}
.pill.ok{background:#E5FAF1;color:#0E9B69}
.pill.warn{background:#FFF3DC;color:#B47508}
.pill .led{width:6px;height:6px;border-radius:50%;background:currentColor}
.relay-metric{display:flex;align-items:center;gap:10px;font-size:.78rem;color:var(--muted);margin-top:8px}
.relay-metric .k{width:84px;flex:none}
.bar{flex:1;height:7px;border-radius:var(--r-pill);background:var(--paper);overflow:hidden}
.bar i{display:block;height:100%;border-radius:var(--r-pill);background:var(--grad)}
.bar i.warn{background:linear-gradient(90deg,#FFAA1D,#FF7A1D)}
.relay-metric .n{font-family:'JetBrains Mono';font-size:.72rem;width:70px;text-align:right;color:var(--text)}

/* ============ ORG TABLE ============ */
.table-card{background:var(--card);border-radius:var(--r-lg);box-shadow:var(--shadow);overflow:hidden;margin-top:14px}
table{width:100%;border-collapse:collapse;font-size:.86rem}
th{
  text-align:left;font-size:.68rem;font-weight:600;letter-spacing:.08em;
  text-transform:uppercase;color:var(--muted);
  padding:14px 20px;border-bottom:1px solid var(--line);background:#FBFBFD;
}
td{padding:15px 20px;border-bottom:1px solid var(--line);vertical-align:middle}
tr:last-child td{border-bottom:none}
tbody tr{transition:background .12s}
tbody tr:hover{background:#FAFAFE}
.org-cell{display:flex;align-items:center;gap:12px}
.org-avatar{
  width:36px;height:36px;border-radius:11px;flex:none;
  display:grid;place-items:center;color:#fff;
  font-family:'Schibsted Grotesk';font-weight:700;font-size:.78rem;
}
.org-cell b{font-weight:600;display:block}
.org-cell span{font-size:.74rem;color:var(--muted)}
.plan{
  display:inline-flex;align-items:center;gap:6px;
  font-size:.7rem;font-weight:600;padding:5px 11px;border-radius:var(--r-pill);
}
.plan.cloud{background:var(--accent-soft);color:var(--accent)}
.plan.self{background:#0B0C15;color:#fff}
.plan svg{width:11px;height:11px;stroke:currentColor;fill:none;stroke-width:2.2;stroke-linecap:round;stroke-linejoin:round}
.num{font-family:'JetBrains Mono';font-size:.8rem}
.health{display:inline-flex;align-items:center;gap:6px;font-size:.78rem;font-weight:500}
.health .led{width:8px;height:8px;border-radius:50%}
.led.g{background:var(--ok)}
.led.y{background:var(--warn)}
.led.r{background:var(--danger)}
.mrr{font-weight:600}
.row-btn{
  padding:7px 14px;border-radius:var(--r-pill);
  border:1px solid var(--line);font-size:.76rem;font-weight:600;color:var(--text);
  transition:all .15s;white-space:nowrap;
}
.row-btn:hover{background:var(--ink);color:#fff;border-color:var(--ink)}

@media(max-width:1100px){.kpis{grid-template-columns:repeat(2,1fr)}.relays{grid-template-columns:1fr}}
@media(max-width:860px){
  body{grid-template-columns:1fr}
  .sidebar{display:none}
  .main{padding:16px}
  .table-card{overflow-x:auto}
}
@media(prefers-reduced-motion:reduce){*{transition:none!important}}
</style>
</head>
<body>

<!-- ================= SIDEBAR (dark = platform level) ================= -->
<aside class="sidebar">
  <div class="brand">
    <div class="brand-mark">
      <svg viewBox="0 0 24 24" fill="none" width="18" height="18"><path d="M3 18 L12 4 L21 18 M7.5 18 L12 11 L16.5 18" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>
    </div>
    <span class="brand-name">Hangar</span>
  </div>
  <span class="hq-tag">PLATFORM CONSOLE</span>

  <div class="nav-label">Overview</div>
  <a class="nav-item active" href="#">
    <svg viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="9" rx="2"/><rect x="14" y="3" width="7" height="5" rx="2"/><rect x="14" y="12" width="7" height="9" rx="2"/><rect x="3" y="16" width="7" height="5" rx="2"/></svg>
    Dashboard
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><path d="M3 21h18M5 21V7l7-4 7 4v14M9 9h1M9 13h1M14 9h1M14 13h1"/></svg>
    Organizations
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><rect x="2" y="4" width="20" height="13" rx="2"/><path d="M8 21h8M12 17v4"/></svg>
    All devices
  </a>

  <div class="nav-label">Infrastructure</div>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3.5 3 14 0 18M12 3c-3 3.5-3 14 0 18"/></svg>
    Relay network
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>
    Live sessions
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="M3.3 7 12 12l8.7-5M12 22V12"/></svg>
    Releases &amp; agents
  </a>

  <div class="nav-label">Business</div>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><rect x="2" y="5" width="20" height="14" rx="3"/><path d="M2 10h20"/></svg>
    Licensing &amp; billing
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M9 15h6M9 11h2"/></svg>
    Audit log
  </a>
  <a class="nav-item" href="#">
    <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h0a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51h0a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v0a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z"/></svg>
    Platform settings
  </a>

  <div class="sidebar-foot">
    <img src="https://i.pravatar.cc/64?img=33" alt="">
    <div>
      <div class="user-name">You</div>
      <div class="user-role">Platform Owner</div>
    </div>
  </div>
</aside>

<!-- ================= MAIN ================= -->
<main class="main">

  <div class="topbar">
    <div class="page-title">
      <h1>Platform overview</h1>
      <p>Friday, Jul 31 · All regions operational</p>
    </div>
    <div class="topbar-spacer"></div>
    <button class="btn-secondary">Invite organization</button>
    <button class="btn-primary">+ New organization</button>
  </div>

  <!-- KPI ROW -->
  <div class="kpis">
    <div class="kpi dark">
      <div class="l">Concurrent sessions</div>
      <div class="v">96 <span class="d">peak 128</span></div>
      <div class="sub">67 P2P · 29 relayed</div>
    </div>
    <div class="kpi">
      <div class="l">P2P success rate</div>
      <div class="v">74% <span class="d">↑ 3%</span></div>
      <div class="sub">every point saves ~€28/mo relay cost</div>
    </div>
    <div class="kpi">
      <div class="l">Organizations</div>
      <div class="v">14 <span class="d">+2 this month</span></div>
      <div class="sub">11 cloud · 3 self-hosted</div>
    </div>
    <div class="kpi">
      <div class="l">Devices enrolled</div>
      <div class="v">1,247 <span class="d">↑ 41</span></div>
      <div class="sub">1,180 online in last 24 h</div>
    </div>
  </div>

  <!-- RELAY NETWORK -->
  <div class="sec-head">
    <h2>Relay network</h2>
    <a href="#">Manage regions →</a>
  </div>
  <div class="relays">
    <div class="relay">
      <div class="relay-top">
        <span class="flag">🇩🇪</span>
        <div><b>EU Central</b><span class="host">relay-eu1.hangar.app</span></div>
        <span class="pill ok"><span class="led"></span>Healthy</span>
      </div>
      <div class="relay-metric"><span class="k">Load</span><div class="bar"><i style="width:38%"></i></div><span class="n">38%</span></div>
      <div class="relay-metric"><span class="k">Bandwidth</span><div class="bar"><i style="width:31%"></i></div><span class="n">312 Mbps</span></div>
      <div class="relay-metric"><span class="k">Sessions</span><div class="bar"><i style="width:22%"></i></div><span class="n">22</span></div>
    </div>
    <div class="relay">
      <div class="relay-top">
        <span class="flag">🇺🇸</span>
        <div><b>US East</b><span class="host">relay-us1.hangar.app</span></div>
        <span class="pill ok"><span class="led"></span>Healthy</span>
      </div>
      <div class="relay-metric"><span class="k">Load</span><div class="bar"><i style="width:17%"></i></div><span class="n">17%</span></div>
      <div class="relay-metric"><span class="k">Bandwidth</span><div class="bar"><i style="width:12%"></i></div><span class="n">118 Mbps</span></div>
      <div class="relay-metric"><span class="k">Sessions</span><div class="bar"><i style="width:6%"></i></div><span class="n">6</span></div>
    </div>
    <div class="relay">
      <div class="relay-top">
        <span class="flag">🇦🇪</span>
        <div><b>Middle East</b><span class="host">relay-me1.hangar.app</span></div>
        <span class="pill warn"><span class="led"></span>High load</span>
      </div>
      <div class="relay-metric"><span class="k">Load</span><div class="bar"><i class="warn" style="width:81%"></i></div><span class="n">81%</span></div>
      <div class="relay-metric"><span class="k">Bandwidth</span><div class="bar"><i class="warn" style="width:74%"></i></div><span class="n">742 Mbps</span></div>
      <div class="relay-metric"><span class="k">Sessions</span><div class="bar"><i style="width:34%"></i></div><span class="n">34</span></div>
    </div>
  </div>

  <!-- ORGANIZATIONS -->
  <div class="sec-head">
    <h2>Organizations</h2>
    <a href="#">View all 14 →</a>
  </div>
  <div class="table-card">
    <table>
      <thead>
        <tr>
          <th>Organization</th>
          <th>Deployment</th>
          <th>Devices</th>
          <th>Users</th>
          <th>Fleet health</th>
          <th>MRR</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td><div class="org-cell"><div class="org-avatar" style="background:#0B0C15">LT</div><div><b>LOT Flight Academy</b><span>Warsaw, PL · since 2025</span></div></div></td>
          <td><span class="plan cloud"><svg viewBox="0 0 24 24"><path d="M17.5 19a4.5 4.5 0 0 0 0-9 6 6 0 0 0-11.6 1.6A4 4 0 0 0 7 19Z"/></svg>Cloud</span></td>
          <td class="num">14</td>
          <td class="num">9</td>
          <td><span class="health"><span class="led y"></span>1 alert</span></td>
          <td class="mrr">€490</td>
          <td><button class="row-btn">Open</button></td>
        </tr>
        <tr>
          <td><div class="org-cell"><div class="org-avatar" style="background:#5B5BF5">SA</div><div><b>SkyWings Academy</b><span>Dubai, AE · since 2026</span></div></div></td>
          <td><span class="plan cloud"><svg viewBox="0 0 24 24"><path d="M17.5 19a4.5 4.5 0 0 0 0-9 6 6 0 0 0-11.6 1.6A4 4 0 0 0 7 19Z"/></svg>Cloud</span></td>
          <td class="num">31</td>
          <td class="num">17</td>
          <td><span class="health"><span class="led g"></span>Healthy</span></td>
          <td class="mrr">€1,085</td>
          <td><button class="row-btn">Open</button></td>
        </tr>
        <tr>
          <td><div class="org-cell"><div class="org-avatar" style="background:#0E9B69">DF</div><div><b>Defence Sim Centre</b><span>Air-gapped · since 2026</span></div></div></td>
          <td><span class="plan self"><svg viewBox="0 0 24 24"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"/></svg>Self-hosted</span></td>
          <td class="num">86</td>
          <td class="num">24</td>
          <td><span class="health"><span class="led g"></span>Phones home ✓</span></td>
          <td class="mrr">€2,400</td>
          <td><button class="row-btn">Open</button></td>
        </tr>
        <tr>
          <td><div class="org-cell"><div class="org-avatar" style="background:#B45CFF">NA</div><div><b>NordAvia Training</b><span>Oslo, NO · trial</span></div></div></td>
          <td><span class="plan cloud"><svg viewBox="0 0 24 24"><path d="M17.5 19a4.5 4.5 0 0 0 0-9 6 6 0 0 0-11.6 1.6A4 4 0 0 0 7 19Z"/></svg>Cloud</span></td>
          <td class="num">6</td>
          <td class="num">3</td>
          <td><span class="health"><span class="led g"></span>Healthy</span></td>
          <td class="mrr">Trial · 9 d left</td>
          <td><button class="row-btn">Open</button></td>
        </tr>
        <tr>
          <td><div class="org-cell"><div class="org-avatar" style="background:#FF7A1D">AT</div><div><b>AeroTech University</b><span>Rzeszów, PL · since 2025</span></div></div></td>
          <td><span class="plan self"><svg viewBox="0 0 24 24"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"/></svg>Self-hosted</span></td>
          <td class="num">42</td>
          <td class="num">11</td>
          <td><span class="health"><span class="led r"></span>License expires 12 d</span></td>
          <td class="mrr">€1,150</td>
          <td><button class="row-btn">Open</button></td>
        </tr>
      </tbody>
    </table>
  </div>

</main>
</body>
</html>
```

---

## Appendix C — Desktop Client (full source, all tabs functional)

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Hangar — Desktop App</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Schibsted+Grotesk:wght@500;700;800&family=Inter:wght@400;500;600&family=JetBrains+Mono:wght@500;600;700&display=swap" rel="stylesheet">
<style>
:root{
  --ink:#0B0C15;
  --ink-2:#141623;
  --paper:#F5F6F9;
  --card:#FFFFFF;
  --line:#E8E9F0;
  --text:#0B0C15;
  --muted:#6E7185;
  --accent:#5B5BF5;
  --accent-soft:#EEEEFE;
  --grad:linear-gradient(135deg,#5B5BF5 0%,#9D5CFF 55%,#3EC8FF 120%);
  --ok:#1FC98B;
  --warn:#FFAA1D;
  --danger:#F5484D;
  --r-lg:22px;
  --r-md:14px;
  --r-pill:999px;
}
*{margin:0;padding:0;box-sizing:border-box}
html{font-size:15px}
body{
  font-family:'Inter',system-ui,sans-serif;color:var(--text);
  -webkit-font-smoothing:antialiased;
  min-height:100vh;display:grid;place-items:center;padding:40px 20px;
  background:
    radial-gradient(900px 500px at 15% 0%,#1B2140,transparent 60%),
    radial-gradient(800px 500px at 90% 100%,#14304A,transparent 60%),
    #0A0D18;
}
button{font-family:inherit;cursor:pointer;border:none;background:none;color:inherit}
:focus-visible{outline:2px solid var(--accent);outline-offset:2px;border-radius:6px}
.mono{font-family:'JetBrains Mono',monospace}

/* ============ APP WINDOW ============ */
.app{
  width:min(980px,100%);
  background:var(--paper);
  border-radius:26px;
  box-shadow:0 40px 100px rgba(0,0,0,.5),0 0 0 1px rgba(255,255,255,.06);
  overflow:hidden;
  display:grid;
  grid-template-rows:auto 1fr auto;
  min-height:640px;
}

/* title bar */
.titlebar{
  display:flex;align-items:center;gap:10px;
  background:var(--card);border-bottom:1px solid var(--line);
  padding:12px 16px;
}
.traffic{display:flex;gap:7px;margin-right:6px}
.traffic i{width:12px;height:12px;border-radius:50%;display:block}
.traffic i:nth-child(1){background:#FF5F57}
.traffic i:nth-child(2){background:#FEBC2E}
.traffic i:nth-child(3){background:#28C840}
.tb-brand{display:flex;align-items:center;gap:8px;font-family:'Schibsted Grotesk';font-weight:800;font-size:1rem;letter-spacing:-.02em}
.tb-mark{width:24px;height:24px;border-radius:8px;background:var(--grad);display:grid;place-items:center}
.tb-org{
  display:flex;align-items:center;gap:7px;
  font-size:.74rem;font-weight:600;color:var(--muted);
  background:var(--paper);border:1px solid var(--line);
  padding:5px 12px;border-radius:var(--r-pill);margin-left:6px;
}
.tb-org b{color:var(--text)}
.tb-spacer{flex:1}
.tb-login{font-size:.78rem;font-weight:600;color:var(--accent)}
.tb-avatar{width:28px;height:28px;border-radius:50%}

/* ============ BODY: icon rail + content ============ */
.body{display:grid;grid-template-columns:64px 1fr;min-height:0}
.rail{
  background:var(--card);border-right:1px solid var(--line);
  display:flex;flex-direction:column;align-items:center;gap:6px;padding:14px 0;
}
.rail-btn{
  width:44px;height:44px;border-radius:14px;
  display:grid;place-items:center;color:var(--muted);
  transition:all .15s;position:relative;
}
.rail-btn svg{width:20px;height:20px;stroke:currentColor;fill:none;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
.rail-btn:hover{background:var(--paper);color:var(--text)}
.rail-btn.active{background:var(--accent-soft);color:var(--accent)}
.rail-btn .bdg{position:absolute;top:7px;right:7px;width:8px;height:8px;border-radius:50%;background:var(--danger);border:2px solid var(--card)}
.rail .push{flex:1}

.content-wrap{min-width:0;overflow-y:auto}
.view{display:none;padding:26px 30px;animation:fade .18s ease}
.view.on{display:block}
@keyframes fade{from{opacity:0;transform:translateY(4px)}to{opacity:1;transform:none}}
.view-title{font-family:'Schibsted Grotesk';font-weight:800;font-size:1.25rem;letter-spacing:-.02em;margin-bottom:4px}
.view-sub{font-size:.8rem;color:var(--muted);margin-bottom:18px}

/* ============ CONNECT VIEW ============ */
.cols{display:grid;grid-template-columns:1fr 1fr;gap:22px;align-items:stretch}
.me{display:flex;flex-direction:column;gap:14px}
.id-card{
  position:relative;overflow:hidden;
  background:var(--ink);color:#fff;
  border-radius:var(--r-lg);
  padding:24px 26px;
  min-height:218px;
  display:flex;flex-direction:column;
}
.id-card::before{
  content:"";position:absolute;inset:0;
  background:
    radial-gradient(420px 260px at 100% -30%,rgba(93,91,245,.6),transparent 62%),
    radial-gradient(340px 220px at -10% 120%,rgba(62,200,255,.3),transparent 60%);
  pointer-events:none;
}
.id-card > *{position:relative}
.idc-top{display:flex;align-items:center;justify-content:space-between}
.idc-label{font-size:.68rem;font-weight:600;letter-spacing:.12em;color:rgba(255,255,255,.55)}
.idc-chip{
  width:34px;height:25px;border-radius:6px;
  background:linear-gradient(135deg,#E8C877,#B98A3E);
  box-shadow:inset 0 0 0 1px rgba(255,255,255,.25);
}
.idc-id{
  font-family:'JetBrains Mono';font-weight:700;font-size:1.9rem;
  letter-spacing:.08em;margin-top:16px;
}
.idc-row{display:flex;align-items:flex-end;justify-content:space-between;margin-top:auto}
.idc-pass .k{font-size:.62rem;font-weight:600;letter-spacing:.12em;color:rgba(255,255,255,.5)}
.idc-pass .v{font-family:'JetBrains Mono';font-weight:600;font-size:1rem;letter-spacing:.2em;margin-top:3px;display:flex;align-items:center;gap:10px}
.idc-pass .v button{color:rgba(255,255,255,.6);display:grid;place-items:center;transition:color .15s}
.idc-pass .v button:hover{color:#fff}
.idc-pass .v svg{width:15px;height:15px;stroke:currentColor;fill:none;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}
.idc-name{text-align:right}
.idc-name .h{font-family:'Schibsted Grotesk';font-weight:700;font-size:.86rem}
.idc-name .s{font-size:.66rem;color:rgba(255,255,255,.5);margin-top:2px}

.me-opts{
  background:var(--card);border-radius:var(--r-lg);
  padding:6px 20px;flex:1;display:flex;flex-direction:column;justify-content:center;
}
.opt{display:flex;align-items:center;gap:12px;padding:13px 0;border-top:1px solid var(--line);font-size:.85rem}
.opt:first-child{border-top:none}
.opt b{font-weight:600;display:block}
.opt span{font-size:.73rem;color:var(--muted)}
.opt .grow{flex:1}
.toggle{
  width:42px;height:24px;border-radius:var(--r-pill);
  background:#D8DAE4;position:relative;transition:background .2s;flex:none;
}
.toggle::after{
  content:"";position:absolute;top:3px;left:3px;width:18px;height:18px;
  border-radius:50%;background:#fff;box-shadow:0 1px 3px rgba(0,0,0,.25);
  transition:left .2s;
}
.toggle.on{background:var(--ok)}
.toggle.on::after{left:21px}

.connect{
  background:var(--card);border-radius:var(--r-lg);
  padding:26px 28px;display:flex;flex-direction:column;
}
.connect h2{font-family:'Schibsted Grotesk';font-weight:800;font-size:1.25rem;letter-spacing:-.02em}
.connect .sub{font-size:.8rem;color:var(--muted);margin-top:4px;margin-bottom:20px}
.field{margin-bottom:12px}
.field label{font-size:.72rem;font-weight:600;color:var(--muted);display:block;margin-bottom:6px}
.input{
  display:flex;align-items:center;gap:11px;
  background:var(--paper);border:1.5px solid transparent;
  border-radius:var(--r-md);padding:13px 15px;
  transition:border-color .15s,background .15s;
}
.input:focus-within{border-color:var(--accent);background:#fff}
.input svg{width:17px;height:17px;stroke:var(--muted);fill:none;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round;flex:none}
.input input{
  border:none;outline:none;background:none;width:100%;
  font-family:'JetBrains Mono';font-weight:600;font-size:1rem;letter-spacing:.06em;color:var(--text);
}
.input input::placeholder{font-family:'Inter';font-weight:400;letter-spacing:0;color:#A5A8BB;font-size:.9rem}
.btn-go{
  margin-top:8px;width:100%;padding:15px 0;
  border-radius:var(--r-pill);background:var(--grad);color:#fff;
  font-weight:700;font-size:.95rem;letter-spacing:.01em;
  transition:transform .15s,box-shadow .15s,opacity .15s;
}
.btn-go:hover{transform:translateY(-1px);box-shadow:0 12px 28px rgba(93,91,245,.4)}
.btn-go:disabled{opacity:.6;transform:none;box-shadow:none;cursor:default}
.mode-hint{
  margin-top:14px;display:flex;align-items:center;justify-content:center;gap:8px;
  font-size:.72rem;color:var(--muted);
}
.mode-hint .led{width:7px;height:7px;border-radius:50%;background:var(--ok);box-shadow:0 0 8px rgba(31,201,139,.7)}
.divider{display:flex;align-items:center;gap:12px;margin:18px 0 14px;color:var(--muted);font-size:.7rem;font-weight:600;letter-spacing:.08em}
.divider::before,.divider::after{content:"";flex:1;height:1px;background:var(--line)}
.quick{display:flex;gap:10px}
.quick button{
  flex:1;display:flex;align-items:center;justify-content:center;gap:8px;
  border:1px solid var(--line);border-radius:var(--r-md);
  padding:11px 0;font-size:.78rem;font-weight:600;color:var(--text);
  transition:all .15s;
}
.quick button:hover{border-color:var(--ink);background:var(--paper)}
.quick svg{width:15px;height:15px;stroke:currentColor;fill:none;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}

.recent-strip{margin-top:22px}
.recent-strip h3{
  font-family:'Schibsted Grotesk';font-weight:700;font-size:.95rem;
  margin-bottom:12px;display:flex;align-items:center;justify-content:space-between;
}
.recent-strip h3 button{font-size:.75rem;color:var(--accent);font-weight:600}
.r-row{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}
.r-card{
  background:var(--card);border-radius:16px;padding:13px 15px;
  display:flex;align-items:center;gap:12px;text-align:left;
  transition:transform .15s,box-shadow .15s;
}
.r-card:hover{transform:translateY(-2px);box-shadow:0 10px 24px rgba(11,12,21,.1)}
.r-thumb{
  width:42px;height:32px;border-radius:8px;flex:none;position:relative;
  background:linear-gradient(150deg,#1B1E33,#0B0C15);overflow:hidden;
}
.r-thumb i{position:absolute;left:5px;top:5px;width:60%;height:5px;border-radius:2px;background:rgba(255,255,255,.12)}
.r-thumb i:nth-child(2){top:13px;width:75%}
.r-thumb .rled{position:absolute;right:4px;bottom:4px;width:7px;height:7px;border-radius:50%;border:1.5px solid #0B0C15}
.rled.g{background:var(--ok)}
.rled.r{background:var(--danger)}
.r-card b{font-weight:600;font-size:.8rem;display:block}
.r-card .rid{font-family:'JetBrains Mono';font-size:.7rem;color:var(--muted);letter-spacing:.05em;margin-top:2px;display:block}

/* ============ LIST VIEWS (recent / address book / transfers / notifications) ============ */
.list-card{background:var(--card);border-radius:var(--r-lg);overflow:hidden}
.lrow{
  display:flex;align-items:center;gap:14px;width:100%;text-align:left;
  padding:15px 20px;border-top:1px solid var(--line);font-size:.86rem;
  transition:background .12s;
}
.lrow:first-child{border-top:none}
button.lrow:hover{background:#FAFAFE}
.lrow .lled{width:9px;height:9px;border-radius:50%;flex:none}
.lled.g{background:var(--ok)}
.lled.r{background:var(--danger)}
.lled.b{background:#3EC8FF}
.lled.y{background:var(--warn)}
.lrow b{font-weight:600;display:block}
.lrow .sub2{font-size:.74rem;color:var(--muted);margin-top:1px}
.lrow .did{font-family:'JetBrains Mono';font-size:.74rem;color:var(--muted);letter-spacing:.05em}
.lrow .end{margin-left:auto;display:flex;align-items:center;gap:10px}
.pill{
  font-size:.68rem;font-weight:600;padding:4px 10px;border-radius:var(--r-pill);white-space:nowrap;
}
.pill.p2p{background:#E5FAF1;color:#0E9B69}
.pill.relay{background:#EAF4FF;color:#2276D2}
.pill.grp{background:var(--accent-soft);color:var(--accent)}
.pill.done{background:#E5FAF1;color:#0E9B69}
.lrow .go{
  flex:none;padding:8px 16px;border-radius:var(--r-pill);
  background:var(--ink);color:#fff;font-size:.76rem;font-weight:600;
  transition:opacity .15s;
}
.lrow .go:hover{opacity:.85}
.lrow .go.off{background:var(--line);color:var(--muted)}
.who{
  width:34px;height:34px;border-radius:50%;flex:none;
  background:var(--accent-soft);color:var(--accent);
  display:grid;place-items:center;font-weight:700;font-size:.72rem;
}
.grp-label{
  font-size:.68rem;font-weight:600;letter-spacing:.08em;text-transform:uppercase;
  color:var(--muted);padding:16px 4px 8px;
}
.prog{width:130px;height:7px;border-radius:var(--r-pill);background:var(--paper);overflow:hidden;flex:none}
.prog i{display:block;height:100%;border-radius:var(--r-pill);background:var(--grad)}
.n-ic{
  width:34px;height:34px;border-radius:11px;flex:none;
  display:grid;place-items:center;color:#fff;
}
.n-ic svg{width:16px;height:16px;stroke:currentColor;fill:none;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}
.n-ic.warn{background:var(--warn)}
.n-ic.info{background:var(--accent)}
.n-ic.ok{background:var(--ok)}

/* ============ SETTINGS-STYLE VIEWS ============ */
.set-card{background:var(--card);border-radius:var(--r-lg);padding:6px 22px;margin-bottom:16px}
.set-card .opt{padding:15px 0}
.sel{
  border:1px solid var(--line);border-radius:var(--r-pill);
  padding:8px 14px;font-size:.78rem;font-weight:600;background:var(--paper);
  font-family:inherit;color:var(--text);
}
.btn-small{
  padding:8px 16px;border-radius:var(--r-pill);
  border:1px solid var(--line);font-size:.76rem;font-weight:600;
  transition:all .15s;
}
.btn-small:hover{background:var(--ink);color:#fff;border-color:var(--ink)}

/* ============ STATUS BAR ============ */
.statusbar{
  display:flex;align-items:center;gap:22px;
  background:var(--card);border-top:1px solid var(--line);
  padding:11px 22px;font-size:.74rem;color:var(--muted);
}
.sb{display:flex;align-items:center;gap:7px}
.sb .led{width:8px;height:8px;border-radius:50%}
.sb b{color:var(--text);font-weight:600}
.sb-spacer{flex:1}
.sb .ver{font-family:'JetBrains Mono';font-size:.68rem}

@media(max-width:820px){
  .cols{grid-template-columns:1fr}
  .r-row{grid-template-columns:repeat(2,1fr)}
  .body{grid-template-columns:1fr}
  .rail{flex-direction:row;border-right:none;border-bottom:1px solid var(--line);padding:8px 14px}
  .rail .push{display:none}
}
@media(prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
</style>
</head>
<body>

<div class="app" role="application" aria-label="Hangar desktop client">

  <!-- ======= TITLE BAR ======= -->
  <div class="titlebar">
    <div class="traffic"><i></i><i></i><i></i></div>
    <div class="tb-brand">
      <span class="tb-mark"><svg viewBox="0 0 24 24" width="13" height="13" fill="none"><path d="M3 18 L12 4 L21 18 M7.5 18 L12 11 L16.5 18" stroke="#fff" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg></span>
      Hangar
    </div>
    <div class="tb-org">Enrolled in <b>LOT Flight Academy</b> · FNPT Sims</div>
    <div class="tb-spacer"></div>
    <button class="tb-login">Log in</button>
    <img class="tb-avatar" src="https://i.pravatar.cc/56?img=12" alt="">
  </div>

  <!-- ======= BODY ======= -->
  <div class="body">

    <!-- icon rail -->
    <nav class="rail" role="tablist" aria-label="Main">
      <button class="rail-btn active" role="tab" aria-selected="true" data-view="connect" aria-label="Connect" title="Connect">
        <svg viewBox="0 0 24 24"><path d="M8 3H5a2 2 0 0 0-2 2v3M16 3h3a2 2 0 0 1 2 2v3M8 21H5a2 2 0 0 1-2-2v-3M16 21h3a2 2 0 0 0 2-2v-3"/><circle cx="12" cy="12" r="3.2"/></svg>
      </button>
      <button class="rail-btn" role="tab" aria-selected="false" data-view="recent" aria-label="Recent connections" title="Recent connections">
        <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>
      </button>
      <button class="rail-btn" role="tab" aria-selected="false" data-view="book" aria-label="Address book" title="Address book">
        <svg viewBox="0 0 24 24"><rect x="4" y="3" width="16" height="18" rx="2"/><circle cx="12" cy="10" r="2.5"/><path d="M8 17c.8-1.8 2.3-2.6 4-2.6s3.2.8 4 2.6"/></svg>
      </button>
      <button class="rail-btn" role="tab" aria-selected="false" data-view="transfers" aria-label="File transfers" title="File transfers">
        <svg viewBox="0 0 24 24"><path d="M12 3v12M7 10l5 5 5-5M4 21h16"/></svg>
      </button>
      <button class="rail-btn" role="tab" aria-selected="false" data-view="notifications" aria-label="Notifications" title="Notifications">
        <svg viewBox="0 0 24 24"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0"/></svg>
        <span class="bdg" id="notif-bdg"></span>
      </button>
      <span class="push"></span>
      <button class="rail-btn" role="tab" aria-selected="false" data-view="security" aria-label="Security" title="Security">
        <svg viewBox="0 0 24 24"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"/></svg>
      </button>
      <button class="rail-btn" role="tab" aria-selected="false" data-view="settings" aria-label="Settings" title="Settings">
        <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h0a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51h0a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v0a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z"/></svg>
      </button>
    </nav>

    <!-- content -->
    <div class="content-wrap">

      <!-- ============ VIEW: CONNECT ============ -->
      <section class="view on" id="view-connect" role="tabpanel">
        <div class="cols">

          <!-- LEFT: this device -->
          <div class="me">
            <div class="id-card">
              <div class="idc-top">
                <span class="idc-label">THIS DEVICE</span>
                <span class="idc-chip" aria-hidden="true"></span>
              </div>
              <div class="idc-id">722 059 036</div>
              <div class="idc-row">
                <div class="idc-pass">
                  <div class="k">ONE-TIME PASSWORD</div>
                  <div class="v">
                    <span id="pw">•••• ••</span>
                    <button id="pw-eye" aria-label="Show password"><svg viewBox="0 0 24 24"><path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7S1 12 1 12Z"/><circle cx="12" cy="12" r="3"/></svg></button>
                    <button id="pw-copy" aria-label="Copy ID and password"><svg viewBox="0 0 24 24"><rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/></svg></button>
                    <button id="pw-regen" aria-label="Regenerate password"><svg viewBox="0 0 24 24"><path d="M21 12a9 9 0 1 1-2.6-6.4M21 3v6h-6"/></svg></button>
                  </div>
                </div>
                <div class="idc-name">
                  <div class="h">ACL-CAN</div>
                  <div class="s">Windows 10 · 19044</div>
                </div>
              </div>
            </div>

            <div class="me-opts">
              <div class="opt">
                <div><b>Unattended access</b><span>Allowed for FNPT Sims technicians</span></div>
                <span class="grow"></span>
                <button class="toggle on" role="switch" aria-checked="true" aria-label="Unattended access"></button>
              </div>
              <div class="opt">
                <div><b>Run at startup</b><span>Agent starts as a system service</span></div>
                <span class="grow"></span>
                <button class="toggle on" role="switch" aria-checked="true" aria-label="Run at startup"></button>
              </div>
              <div class="opt">
                <div><b>Ask before each session</b><span>Show consent prompt on this screen</span></div>
                <span class="grow"></span>
                <button class="toggle" role="switch" aria-checked="false" aria-label="Ask before each session"></button>
              </div>
            </div>
          </div>

          <!-- RIGHT: connect -->
          <div class="connect">
            <h2>Control a remote computer</h2>
            <p class="sub">Enter the device ID shown on the remote screen, or pick one from your fleet.</p>

            <div class="field">
              <label for="rid">Remote ID</label>
              <div class="input">
                <svg viewBox="0 0 24 24"><rect x="2" y="4" width="20" height="13" rx="2"/><path d="M8 21h8M12 17v4"/></svg>
                <input id="rid" placeholder="000 000 000" inputmode="numeric" autocomplete="off">
              </div>
            </div>
            <div class="field">
              <label for="rpw">Password</label>
              <div class="input">
                <svg viewBox="0 0 24 24"><rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/></svg>
                <input id="rpw" type="password" placeholder="Session or custom password" autocomplete="off">
              </div>
            </div>

            <button class="btn-go" id="go">Connect now</button>
            <div class="mode-hint"><span class="led"></span><span id="hint-text">Direct P2P available · relay-eu1 standby · 14 ms</span></div>

            <div class="divider">OR JUMP BACK IN</div>
            <div class="quick">
              <button id="q-file"><svg viewBox="0 0 24 24"><path d="M12 3v12M7 10l5 5 5-5"/></svg>File transfer only</button>
              <button id="q-term"><svg viewBox="0 0 24 24"><path d="m4 17 6-6-6-6M12 19h8"/></svg>Terminal only</button>
            </div>
          </div>

        </div>

        <!-- RECENT STRIP -->
        <div class="recent-strip">
          <h3>Recent connections <button data-goto="recent">View all →</button></h3>
          <div class="r-row">
            <button class="r-card" data-id="688 630 782">
              <span class="r-thumb"><i></i><i></i><span class="rled g"></span></span>
              <span><b>MDS-01</b><span class="rid">688 630 782</span></span>
            </button>
            <button class="r-card" data-id="421 250 992">
              <span class="r-thumb"><i></i><i></i><span class="rled g"></span></span>
              <span><b>DISPLAY-PC</b><span class="rid">421 250 992</span></span>
            </button>
            <button class="r-card" data-id="235 528 227">
              <span class="r-thumb"><i></i><i></i><span class="rled g"></span></span>
              <span><b>WS-25-008</b><span class="rid">235 528 227</span></span>
            </button>
            <button class="r-card" data-id="167 674 074" data-off="1">
              <span class="r-thumb"><i></i><i></i><span class="rled r"></span></span>
              <span><b>HOST-5884</b><span class="rid">167 674 074</span></span>
            </button>
          </div>
        </div>
      </section>

      <!-- ============ VIEW: RECENT ============ -->
      <section class="view" id="view-recent" role="tabpanel" hidden>
        <div class="view-title">Recent connections</div>
        <div class="view-sub">Your last sessions from this device.</div>
        <div class="list-card">
          <button class="lrow" data-id="688 630 782">
            <span class="lled g"></span>
            <span><b>MDS-01</b><span class="sub2">Remote control · today 09:15 · 24 min</span></span>
            <span class="end"><span class="pill p2p">P2P</span><span class="go">Reconnect</span></span>
          </button>
          <button class="lrow" data-id="421 250 992">
            <span class="lled g"></span>
            <span><b>DISPLAY-PC</b><span class="sub2">File transfer · today 08:40 · scenery_2.4.zip</span></span>
            <span class="end"><span class="pill p2p">P2P</span><span class="go">Reconnect</span></span>
          </button>
          <button class="lrow" data-id="235 528 227">
            <span class="lled g"></span>
            <span><b>WS-25-008</b><span class="sub2">Terminal · yesterday 16:02 · 6 min</span></span>
            <span class="end"><span class="pill relay">Relay</span><span class="go">Reconnect</span></span>
          </button>
          <button class="lrow" data-id="167 674 074" data-off="1">
            <span class="lled r"></span>
            <span><b>HOST-5884</b><span class="sub2">Remote control · Jul 29 · 41 min</span></span>
            <span class="end"><span class="pill relay">Relay</span><span class="go off">Offline</span></span>
          </button>
        </div>
      </section>

      <!-- ============ VIEW: ADDRESS BOOK ============ -->
      <section class="view" id="view-book" role="tabpanel" hidden>
        <div class="view-title">Address book</div>
        <div class="view-sub">Devices you're allowed to control, by group.</div>

        <div class="grp-label">FNPT Sims · 3 devices</div>
        <div class="list-card">
          <button class="lrow" data-id="722 059 036">
            <span class="lled g"></span>
            <span><b>ACL-CAN</b><span class="did">722 059 036</span></span>
            <span class="end"><span class="pill grp">This device</span></span>
          </button>
          <button class="lrow" data-id="688 630 782">
            <span class="lled g"></span>
            <span><b>MDS-01</b><span class="did">688 630 782</span></span>
            <span class="end"><span class="go">Connect</span></span>
          </button>
          <button class="lrow" data-id="235 528 227">
            <span class="lled g"></span>
            <span><b>WS-25-008</b><span class="did">235 528 227</span></span>
            <span class="end"><span class="go">Connect</span></span>
          </button>
        </div>

        <div class="grp-label">FTD Bay · 1 device</div>
        <div class="list-card">
          <button class="lrow" data-id="421 250 992">
            <span class="lled g"></span>
            <span><b>DISPLAY-PC</b><span class="did">421 250 992</span></span>
            <span class="end"><span class="go">Connect</span></span>
          </button>
        </div>

        <div class="grp-label">Briefing Rooms · 1 device</div>
        <div class="list-card">
          <button class="lrow" data-id="167 674 074" data-off="1">
            <span class="lled r"></span>
            <span><b>HOST-5884</b><span class="did">167 674 074</span></span>
            <span class="end"><span class="go off">Offline</span></span>
          </button>
        </div>
      </section>

      <!-- ============ VIEW: TRANSFERS ============ -->
      <section class="view" id="view-transfers" role="tabpanel" hidden>
        <div class="view-title">File transfers</div>
        <div class="view-sub">Active and recent transfers from this device.</div>
        <div class="list-card">
          <div class="lrow">
            <span class="lled b"></span>
            <span><b>scenery_update_2.5.zip</b><span class="sub2">→ MDS-01 · 1.4 GB of 2.4 GB</span></span>
            <span class="end"><span class="prog"><i id="tprog" style="width:58%"></i></span><span class="sub2" id="tpct">58%</span></span>
          </div>
          <div class="lrow">
            <span class="lled g"></span>
            <span><b>fnpt_config_backup.7z</b><span class="sub2">← ACL-CAN · 312 MB · today 08:41</span></span>
            <span class="end"><span class="pill done">Done</span></span>
          </div>
          <div class="lrow">
            <span class="lled g"></span>
            <span><b>license_key_2026.dat</b><span class="sub2">→ WS-25-008 · 4 KB · yesterday</span></span>
            <span class="end"><span class="pill done">Done</span></span>
          </div>
        </div>
      </section>

      <!-- ============ VIEW: NOTIFICATIONS ============ -->
      <section class="view" id="view-notifications" role="tabpanel" hidden>
        <div class="view-title">Notifications</div>
        <div class="view-sub">Alerts from your fleet and this device.</div>
        <div class="list-card">
          <div class="lrow">
            <span class="n-ic warn"><svg viewBox="0 0 24 24"><path d="M12 9v4M12 17h.01M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/></svg></span>
            <span><b>GPU 84 °C on this device</b><span class="sub2">Above the 80 °C policy limit · 8 min ago</span></span>
          </div>
          <div class="lrow">
            <span class="n-ic info"><svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 8h.01M12 12v4"/></svg></span>
            <span><b>Hangar Support access granted</b><span class="sub2">Expires in 6 days · granted by Marek K. · yesterday</span></span>
          </div>
          <div class="lrow">
            <span class="n-ic ok"><svg viewBox="0 0 24 24"><path d="M20 6 9 17l-5-5"/></svg></span>
            <span><b>Agent updated to 1.4.2</b><span class="sub2">Restart not required · Jul 28</span></span>
          </div>
        </div>
      </section>

      <!-- ============ VIEW: SECURITY ============ -->
      <section class="view" id="view-security" role="tabpanel" hidden>
        <div class="view-title">Security</div>
        <div class="view-sub">Who can reach this device, and how sessions are protected.</div>
        <div class="set-card">
          <div class="opt">
            <div><b>Custom device password</b><span>Extra password on top of one-time codes</span></div>
            <span class="grow"></span>
            <button class="btn-small">Set password</button>
          </div>
          <div class="opt">
            <div><b>Two-factor confirmation</b><span>Approve incoming sessions in the mobile app</span></div>
            <span class="grow"></span>
            <button class="toggle" role="switch" aria-checked="false" aria-label="Two-factor confirmation"></button>
          </div>
          <div class="opt">
            <div><b>Lock on session end</b><span>Lock Windows when a remote session closes</span></div>
            <span class="grow"></span>
            <button class="toggle on" role="switch" aria-checked="true" aria-label="Lock on session end"></button>
          </div>
          <div class="opt">
            <div><b>Allowed connections</b><span>Who may start a session to this device</span></div>
            <span class="grow"></span>
            <select class="sel" aria-label="Allowed connections">
              <option>My organization only</option>
              <option>Assigned groups only</option>
              <option>Anyone with ID + password</option>
            </select>
          </div>
        </div>
        <div class="set-card">
          <div class="opt">
            <div><b>End-to-end encryption</b><span>DTLS 1.3 · relays never see screen data</span></div>
            <span class="grow"></span>
            <span class="pill done">Active</span>
          </div>
        </div>
      </section>

      <!-- ============ VIEW: SETTINGS ============ -->
      <section class="view" id="view-settings" role="tabpanel" hidden>
        <div class="view-title">Settings</div>
        <div class="view-sub">How the agent behaves on this machine.</div>
        <div class="set-card">
          <div class="opt">
            <div><b>Unattended access</b><span>Allowed for FNPT Sims technicians</span></div>
            <span class="grow"></span>
            <button class="toggle on" role="switch" aria-checked="true" aria-label="Unattended access"></button>
          </div>
          <div class="opt">
            <div><b>Run at startup</b><span>Agent starts as a system service</span></div>
            <span class="grow"></span>
            <button class="toggle on" role="switch" aria-checked="true" aria-label="Run at startup"></button>
          </div>
          <div class="opt">
            <div><b>Ask before each session</b><span>Show consent prompt on this screen</span></div>
            <span class="grow"></span>
            <button class="toggle" role="switch" aria-checked="false" aria-label="Ask before each session"></button>
          </div>
        </div>
        <div class="set-card">
          <div class="opt">
            <div><b>Video quality</b><span>Balance sharpness and bandwidth</span></div>
            <span class="grow"></span>
            <select class="sel" aria-label="Video quality">
              <option>Auto (adaptive)</option>
              <option>Best quality</option>
              <option>Lowest bandwidth</option>
            </select>
          </div>
          <div class="opt">
            <div><b>Updates</b><span>Channel controlled by LOT Flight Academy policy</span></div>
            <span class="grow"></span>
            <span class="pill grp">Stable · managed</span>
          </div>
        </div>
      </section>

    </div>
  </div>

  <!-- ======= STATUS BAR ======= -->
  <div class="statusbar">
    <span class="sb"><span class="led" style="background:var(--ok)"></span>Connected · <b>relay-eu1.hangar.app</b></span>
    <span class="sb"><span class="led" style="background:var(--ok)"></span><b>System access granted</b></span>
    <span class="sb"><span class="led" style="background:#3EC8FF"></span>End-to-end encrypted</span>
    <span class="sb-spacer"></span>
    <span class="sb ver">Agent 1.4.2 · up to date</span>
  </div>

</div>

<script>
// ---------- tab switching ----------
const railBtns = document.querySelectorAll('.rail-btn');
function showView(name){
  railBtns.forEach(b => {
    const on = b.dataset.view === name;
    b.classList.toggle('active', on);
    b.setAttribute('aria-selected', on);
  });
  document.querySelectorAll('.view').forEach(v => {
    const on = v.id === 'view-' + name;
    v.classList.toggle('on', on);
    v.hidden = !on;
  });
  if (name === 'notifications') {
    const bdg = document.getElementById('notif-bdg');
    if (bdg) bdg.remove();
  }
}
railBtns.forEach(b => b.addEventListener('click', () => showView(b.dataset.view)));
document.querySelectorAll('[data-goto]').forEach(b =>
  b.addEventListener('click', () => showView(b.dataset.goto)));

// ---------- password reveal / copy / regen ----------
const pw = document.getElementById('pw');
let secret = 'k4v9m2';
let shown = false;
function renderPw(){ pw.textContent = shown ? secret : '\u2022\u2022\u2022\u2022 \u2022\u2022'; }
document.getElementById('pw-eye').addEventListener('click', () => { shown = !shown; renderPw(); });
document.getElementById('pw-copy').addEventListener('click', e => {
  if (navigator.clipboard) navigator.clipboard.writeText('ID 722 059 036 / ' + secret);
  const btn = e.currentTarget;
  btn.style.color = '#5CE6B8';
  setTimeout(() => btn.style.color = '', 900);
});
document.getElementById('pw-regen').addEventListener('click', () => {
  secret = Math.random().toString(36).slice(2, 8);
  shown = true; renderPw();
  setTimeout(() => { shown = false; renderPw(); }, 2500);
});

// ---------- toggles ----------
document.querySelectorAll('.toggle').forEach(t => t.addEventListener('click', () => {
  const on = t.classList.toggle('on');
  t.setAttribute('aria-checked', on);
}));

// ---------- connect flow (demo) ----------
const go = document.getElementById('go');
const hintText = document.getElementById('hint-text');
const HINT_DEFAULT = 'Direct P2P available \u00b7 relay-eu1 standby \u00b7 14 ms';
go.addEventListener('click', () => {
  const rid = document.getElementById('rid');
  if (!rid.value.trim()) { rid.focus(); return; }
  go.disabled = true;
  go.textContent = 'Connecting\u2026';
  hintText.textContent = 'Negotiating direct P2P link\u2026';
  setTimeout(() => {
    go.textContent = 'Connected \u2713';
    hintText.textContent = 'Session established \u00b7 P2P \u00b7 encrypted';
    setTimeout(() => {
      go.disabled = false;
      go.textContent = 'Connect now';
      hintText.textContent = HINT_DEFAULT;
    }, 1800);
  }, 1400);
});

// quick actions reuse the same flow
['q-file','q-term'].forEach(id =>
  document.getElementById(id).addEventListener('click', () => {
    const rid = document.getElementById('rid');
    if (!rid.value.trim()) { rid.focus(); return; }
    go.click();
  }));

// ---------- any device row prefills Connect ----------
document.querySelectorAll('.r-card, .lrow[data-id]').forEach(r =>
  r.addEventListener('click', e => {
    if (r.dataset.off) return;
    if (e.target.classList.contains('pill')) return;
    document.getElementById('rid').value = r.dataset.id;
    showView('connect');
    document.getElementById('rpw').focus();
  }));

// ---------- fake transfer progress ----------
let pct = 58;
setInterval(() => {
  if (pct < 100) {
    pct = Math.min(100, pct + Math.random() * 3);
    const bar = document.getElementById('tprog');
    const label = document.getElementById('tpct');
    if (bar) bar.style.width = pct.toFixed(0) + '%';
    if (label) label.textContent = pct >= 100 ? 'Done' : pct.toFixed(0) + '%';
  }
}, 1200);
</script>
</body>
</html>
```
