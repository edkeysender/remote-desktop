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
  multi-monitor, clipboard sync, file transfer, adaptive bitrate.
- **Phase 4 — product:** host as a **Windows Service** (survives logout, works at the
  lock screen / UAC secure desktop via `WTSQueryUserToken` + `CreateProcessAsUser`),
  installers, **code signing**, accounts, self-hosted relay deployment.

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
