# Architecture & Roadmap

## Components

### 1. Signaling server (`server/`, Node + ws)
The only always-on public piece. Its job is to let two machines behind NAT find each
other by ID. It must **never** see pixels (Phase 2+) and **never** learn the password
(the host verifies it). In Phase 0 it also relays frames/input as a shortcut — that
relay role goes away once WebRTC is in.

Responsibilities:
- Assign each host a stable **ID** on registration.
- Route a viewer's `connect` request to the right host by ID.
- Let the host accept/reject (it checks the password locally).
- Phase 0 only: pipe binary frames host→viewer and input JSON viewer→host.
- Phase 2+: relay SDP offers/answers and ICE candidates, then get out of the way.
- Serve app auto-updates over HTTP on the same port: `GET /update/manifest.json`
  (version + per-component file list) and `GET /update/<file>` (published exes),
  from `UPDATE_DIR` (default `./update`). `build.ps1` stages that directory and can
  scp it to the Pi. The Windows apps poll the manifest and offer an in-app update.
- Host a **multi-tenant account web app** (Express, `server/lib/*` + `server/public/`)
  at `/`: register an org (owner=admin), sign in, invite users (link tokens), create
  groups, assign users to groups, and manage computers. Auth is bcrypt + HMAC-signed
  session tokens (cookie for the web, `Bearer` for the desktop app). Data is JSON files
  under `DATA_DIR` (`db.json` + `session-secret`). Access rule: a user may connect to a
  computer iff same org AND (admin OR shares a group with it).
    - A **host claims a computer by signing in**: `register` carries `{token(device),
      auth}`; the relay links that device to the caller's org and it appears in the org's
      computer list.
    - **Or enrolls via a pre-baked token** (Remotler plan Phase 0): an admin generates an
      enrollment token (optionally group-pinned) at `/api/enroll-tokens`; the app sends it
      as `register {enroll}` and the PC is claimed into the org with no interactive login.
      The dashboard (Remotler-branded: device grid, groups, users, enrollment) shows it
      online and it's controllable through the relay by org members. This satisfies the
      Phase 0 exit criteria in `claude/remotler-development-plan-with-html.md` §5 on the
      existing stack (see that file for the full Rust/Go/Postgres target architecture).
    - **Password-less connect by membership**: `connect` carries `{id, auth}`; the relay
      authorizes (org + group) and vouches to the host (`admin:true`), which then accepts
      without the per-client password. `GET /api/my-computers` gives a user their allowed
      groups+computers for the desktop picker.
  - The legacy `ADMIN_PASSWORD` admin panel (`/admin`) and `/directory` are kept for
    backward compatibility with 0.2.x apps; the account system supersedes them.

### Unified desktop app (`src/Master` → `RemoteControl.exe`, Phase 2)
One app is both host and viewer. It signs into an account (email/password → Bearer
token via `Shared/AccountClient`), hosts this PC **always-on** (shows ID + an editable
fixed password; registers with the account token so the PC is claimed into the org),
and lets the signed-in user browse their allowed groups/computers and open each session
in its own `ViewerWindow` — **password-less** via the account. A manual ID+password
connect is still available for outside machines. The host engine (`HostSession`,
`ScreenCapture`, `InputInjector`) is reused from the Client project via a project
reference; the viewer engine (`ViewerSession`) lives here. The standalone Client app +
Windows service remain for **unattended** headless hosting. Update component is `app`.

### Device commands, task manager, WOL, configurations, MCP
- **Command channel:** the relay can send a host an admin command over its signaling
  connection and await a reply (`sendCommand` → `{t:'cmd',reqId,…}` → host `HostCommands`/
  `ConfigApply` → `{t:'cmd-result',reqId,…}`). No WebRTC session needed.
- **Task manager:** `tasklist` / `kill` per online device; `GET /api/computers/:dt/tasks`,
  `POST …/kill`; shown in the host admin panel.
- **Wake-on-LAN:** hosts report their MAC on register; `POST …/wake` picks an online peer
  in the org and has it broadcast the magic packet (`WakeOnLan`).
- **Device configurations:** per-org named configs (wallpaper, login background,
  computer-name standard, Windows-activation/VC++/OpenSSH checks) assigned per device and
  applied via the `config` command; `ConfigApply` does what the current user can and reports
  `needs-admin` for elevation-only actions (the SYSTEM service will run those later). New
  checks slot into `ConfigApply`.
- **Per-org MCP server** (`server/mcp`, `@modelcontextprotocol/sdk`): tools `get_computers`,
  `get_online_computers`, `get_tasks`, `kill_task`, authenticated by an **org API token**
  (web → MCP tab; recognized by the web auth middleware as a manager-scoped principal).

### LAN-direct connections & network settings
Media/input/files travel over WebRTC. ICE gathers **host candidates** (each machine's
local IP), which have the highest priority — so two computers on the same internal network
connect **directly peer-to-peer over the LAN**; the relay only brokers the SDP/ICE handshake
and never carries the stream. A TURN relay is used only as a last resort for symmetric NAT.
Per-org **network settings** (`/api/ice`, web → Configurations → Network) let a fleet run
self-contained: point STUN/TURN at your own coturn, or clear STUN entirely for a flat LAN
(host candidates only → nothing external). The relay delivers the org's ICE config in the
host `registered` and viewer `connected` messages; `Shared/IceConfig` builds it. Default is
a public STUN server, so behavior is unchanged until an admin sets it.

### Branding / white-label
The brand mark is `assets/remotler.ico` (generated by `tools/gen-icon.ps1` — gradient rounded
square + the Remotler roof glyph, multi-size BMP frames); it's the exe/installer icon for all
apps. **Per-org runtime branding**: an org admin sets an app name, accent colour, and logo in
the web app (Branding tab → `PUT /api/branding`, stored on the org). The relay includes the
org's branding in the host `registered` message and `/api/me`, so the desktop app (and the web
dashboard) re-theme themselves per org — no rebuild needed. **Per-org custom exe** (for a
distinct file icon + installed name): `build.ps1 -BrandName "Acme" -BrandIcon acme.ico` bakes
the icon into the exe and emits `Acme-Setup-<ver>.exe` (brand values passed to Inno via
`REMOTLER_BRAND_*` env vars). This must run on a Windows build box — the Linux/Pi relay cannot
compile Windows binaries.

### 2. Host (`host/`, C# .NET 8, Windows-only)
Runs on the controlled machine.
- **Capture:** Phase 0 uses GDI `Graphics.CopyFromScreen` + JPEG (simple). Phase 3
  swaps to **DXGI Desktop Duplication** (dirty-rects, no per-frame full copy) +
  **QuickSync H.264** via Media Foundation (this box has Intel Iris Xe → HW encode).
- **Input:** `SendInput` via P/Invoke. Mouse uses absolute normalized coords over the
  virtual desktop (`MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`). Keyboard uses
  **scancodes** (`KEYEVENTF_SCANCODE`) so raw-input apps/games work.
- **Auth:** verifies the viewer's password against a local Argon2id hash (Phase 1);
  Phase 0 uses a plaintext compare for speed of iteration.
- **Net:** `ClientWebSocket` now; `SIPSorcery` WebRTC peer from Phase 2.

### 3. Viewer (`viewer/`, browser)
- Renders frames to a `<canvas>` (Phase 0) or a `<video>` media track (Phase 2+).
- Captures mouse move/click/scroll and keyboard, normalizes coords to 0..1 of the
  displayed image, sends over the wire (WebSocket now, `RTCDataChannel` later).
- Plain HTML/JS for Phase 0 to avoid build tooling. Upgrade to React + TS + Vite when
  the UI grows (connection manager, multi-monitor picker, settings).

## Wire protocol (Phase 0)

Text frames are JSON with a `t` (type) field. Binary frames are JPEG image data
(host→viewer only). One host ↔ one viewer for now.

**Control**
| From   | Message | Meaning |
|--------|---------|---------|
| host→srv   | `{t:"register"}` | ask for an ID |
| srv→host   | `{t:"registered", id}` | assigned 9-digit ID |
| viewer→srv | `{t:"connect", id, password}` | request a session |
| srv→host   | `{t:"connect-request", rid, password}` | someone wants in (`rid` = request id) |
| host→srv   | `{t:"connect-response", rid, ok}` | password check result |
| srv→viewer | `{t:"connected"}` / `{t:"rejected", reason}` | session up / denied |
| host→viewer| `{t:"screen", w, h}` | source resolution (Phase-0 relay only) |
| either→srv | `{t:"bye"}` | teardown |

**WebRTC signaling (Phase 2)** — relayed verbatim between the paired peers:
| From | Message | Meaning |
|------|---------|---------|
| host→master   | `{t:"offer", sdp}`  | host's SDP offer (video sendonly + data m-line) |
| master→host   | `{t:"answer", sdp}` | master's SDP answer |
| either        | `{t:"ice", candidate}` | trickled ICE candidate |

**File transfer + remote browsing** — on a second host-created data channel labelled
`file` (separate from `input` so a large transfer can't stall input events). Reliable
+ ordered SCTP, so the protocol is minimal; one transfer at a time in either direction
(binary chunks carry no id). Implemented once in `Shared/FileTransferChannel.cs`, used
by both sides. All transfers are **driven from the master** — the client has no transfer
UI, it only answers listing/pull requests and receives uploads. The master's "Transfer
files" button opens a remote file browser (`Master/RemoteBrowserWindow`).
| From | Message | Meaning |
|------|---------|---------|
| sender   | `{t:"begin", name, size, dest?}` | announce one file (`dest` = target dir, else Downloads) |
| sender   | *binary* (16 KB chunks)   | file bytes, in order |
| sender   | `{t:"end"}`               | all bytes sent |
| receiver | `{t:"ack", n}`            | bytes received — sender stalls at >1 MB unacked |
| receiver | `{t:"done"}` / `{t:"err", msg}` | saved ok (to Downloads; Public Downloads under SYSTEM) / failed |
| master   | `{t:"ls", path}`          | list a remote directory (`""` = drive list) |
| client   | `{t:"ls-ok", path, entries:[{n,d,s}]}` / `{t:"ls-err", path, msg}` | listing / error |
| master   | `{t:"get", path}`         | pull a remote file (client replies with `begin`/chunks/`end`) |
| client   | `{t:"get-err", msg}`      | the pull couldn't start |

Exposing the client's whole filesystem to the master is consistent with the trust model
(the master already has full mouse/keyboard control). Remote names are stripped to bare
filenames on save, and uploads only write into an already-existing target directory.

**Media / input**
| From        | Payload | Meaning |
|-------------|---------|---------|
| host→viewer | *binary* | one JPEG frame |
| viewer→host | `{t:"m", x, y}` | mouse move, x/y normalized 0..1 |
| viewer→host | `{t:"b", x, y, btn, down}` | button (0=L,1=R,2=M), down=bool |
| viewer→host | `{t:"w", dy}` | wheel delta |
| viewer→host | `{t:"k", code, down}` | key by `KeyboardEvent.code`, down=bool |

Coords are normalized on the viewer against the *displayed image* so DPI/scaling on
either side doesn't corrupt them; the host maps 0..1 → virtual-desktop absolute.

## Roadmap

- **Phase 0 — spike (here):** WS relay + JPEG + canvas. Prove handshake, coord
  mapping, key translation. LAN / own-VPS only.
- **Phase 1 — auth done right:** server-assigned IDs, host-side Argon2id password
  verify, first-connect "Allow?" prompt on host, ID-enumeration rate limiting.
- **Phase 2 — WebRTC (DONE):** server is pure signaling; it relays SDP offer/answer +
  ICE via its generic paired-peer JSON passthrough (no server code specific to WebRTC).
  Video travels on a VP8 media track (pure-C# `SIPSorceryMedia.Encoders`, native
  `vpxmd.dll`), input on an `input` data channel (host is offerer + creates the channel;
  master answers and sends input back over it). DTLS-SRTP + NAT traversal via a public
  STUN server; coturn is the TURN fallback for symmetric-NAT peers. Verified end-to-end
  with the real session classes: capture → VP8 → P2P → decode at 1920×1080, plus input.
  Note: on the answerer, SIPSorcery's received-channel `onopen` does not fire — readiness
  is detected via `RTCDataChannel.IsOpened`. Codec teardown is lock-guarded against the
  capture pump (a mid-encode dispose is an uncatchable native AccessViolation).
- **Phase 3 — quality:** DXGI Desktop Duplication w/ dirty-rects, QuickSync H.264,
  adaptive bitrate, clipboard sync. Done so far: multi-monitor (numbered screen-icon
  picker in the master toolbar, `selmon` over signaling; only the selected monitor is
  captured/encoded/streamed — "All monitors" is the sole multi-capture mode),
  **file transfer + remote file browser** (master-driven, both directions over the
  `file` data channel), a **Windows-key (Start) button** in the master toolbar, and
  **viewer zoom** — Fit mode scales to the window; 50/100/150/200 % render at pixel
  scale inside a ScrollViewer with capped time-based edge-pan (push the pointer against
  a viewport edge) + Ctrl+wheel zoom, so a 4K remote stays operable on a smaller viewer.
- **Phase 4 — product (IN PROGRESS):** host as a **Windows Service** (survives logout,
  works at the lock screen / UAC secure desktop). `src/Service` is a LocalSystem
  `BackgroundService` (`Supervisor`) that keeps one capture worker — `RemotlerAgent.exe
  --worker` — alive in the active console session, respawning it on session change,
  input-desktop switch (Default ↔ Winlogon), or worker exit, via a duplicated SYSTEM
  token retargeted at the session + `CreateProcessAsUser` onto `WinSta0\{desktop}`
  (`SessionLauncher`). The worker is headless: it reads machine config from
  `%ProgramData%\Remotler`, self-provisions a stable `HostToken` + fixed password on
  first run, and reconnects forever. The relay maps that token → a **stable ID** across
  restarts (`idmap.json`). Wired into the solution, `build.ps1` (published into the
  client folder), and the client installer as an **opt-in** task that `sc create`s +
  starts the service. The attended client window surfaces the unattended ID + fixed
  password when the service has provisioned them (read from the world-readable
  `%ProgramData%\Remotler\machine.json`; the ID field polls until the worker connects).
  Still TODO: provision the real `ServerUrl` at deploy time (defaults to
  `ws://localhost:8080`); **code signing**; accounts; self-hosted relay deployment.
- **In-app auto-update:** both apps check `<http>/update/manifest.json` on launch and,
  if the manifest version beats the running assembly, show an "Update available" button.
  Clicking downloads the component's files (SHA-256 verified) into a temp stage, then a
  PowerShell script waits for the app to exit, swaps the files, and relaunches. The
  **client** elevates (Program Files + it stops/starts the SYSTEM service so the worker
  releases `RemotlerAgent.exe`); the **master** installs per-user and updates without
  a UAC prompt. Version is single-sourced from the `VERSION` file, stamped into the
  assemblies, installers, and manifest by `build.ps1`. See `Shared/Updater.cs`.

## Known hard problems (don't be surprised)
1. **UAC / secure desktop:** a normal user process can't capture or inject into the
   UAC prompt, Ctrl+Alt+Del, or lock screen. Needs the Phase-4 SYSTEM service that
   spawns a capture worker into the active session and re-spawns on desktop switch.
2. **Coordinate mapping:** per-monitor DPI + multi-monitor virtual desktop with
   negative origins. Absolute normalized coords over the virtual desktop is the fix.
3. **Keyboard:** map `KeyboardEvent.code` → PS/2 scancode (a hand-written table) and
   inject with `KEYEVENTF_SCANCODE`, not virtual keys.
4. **Latency budget:** capture + encode + RTT + decode/present. <60ms feels good,
   >120ms feels like mud. Measure each stage.
5. **Antivirus:** capture + synthetic input + network is the RAT signature. Code-sign
   the host, keep the visible session indicator, never enable covert operation.
