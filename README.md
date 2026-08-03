# remote-desktop

A self-built remote desktop tool (TeamViewer/RustDesk architecture). The **Master**
app enters an **ID + password** and connects to the **Client** app running on a
remote machine, sees its screen, and controls mouse + keyboard.

```
 MASTER (.exe)  <--signal-->  SIGNALING SERVER  <--signal-->  CLIENT (.exe)
  control a PC        (Node + ws, on a VPS)         the PC being controlled
   render + input                                    capture + input inject
        \___________________ media / input _______________________/
                    Phase 0: relayed via server
                    Phase 2+: direct P2P WebRTC
```

Both Master and Client are native Windows apps that ship as **single-file installers**
(no .NET install needed on the target machine — the runtime is bundled).

## Current phase: **Phase 2 — WebRTC P2P** (working, verified end-to-end)

Video and input now flow **directly peer-to-peer** over WebRTC; the server only
brokers the introduction (ID/password) and relays SDP/ICE.
- Client captures the screen (GDI), VP8-encodes each frame (pure-C#
  `SIPSorceryMedia.Encoders`), and sends it on a WebRTC video track.
- Master receives the track, decodes VP8 to a `WriteableBitmap`, and renders it.
- Input travels master→client on a WebRTC **data channel**; Client replays with `SendInput`.
- Encryption (DTLS-SRTP), NAT traversal (STUN, with coturn as TURN fallback), and
  congestion control come from WebRTC. The relay server never sees pixels or input.

Verified end-to-end with the real session classes: `capture → VP8 → P2P → decode`
at 1920×1080 plus a live input channel. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the protocol, security model,
and the Phase 0–4 roadmap.

## Repo layout
```
remote-desktop/
├─ server/            Node + ws — signaling & Phase-0 relay (runs on a VPS)
├─ src/
│   ├─ Shared/        signaling WebSocket wrapper + config
│   ├─ Client/        WPF app — capture, VP8 encode, input injection, WebRTC peer
│   └─ Master/        WPF app — WebRTC peer, VP8 decode/render, input capture
├─ tests/E2E/         headless end-to-end test driving the real session classes
├─ installer/         Inno Setup scripts → dist\*-Setup-*.exe
├─ docker/            coturn (TURN relay) config — used from Phase 2
├─ viewer/            legacy browser viewer (still works; Master exe supersedes it)
├─ build.ps1          publish both apps + compile both installers
└─ RemoteDesktop.sln
```

## Build the installers
Requires the .NET 8 SDK and Inno Setup 6 (both installable via `winget`):
```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup

powershell -ExecutionPolicy Bypass -File build.ps1
```
Produces:
```
installer\dist\HangarAgent-Setup-0.1.0.exe   (~70 MB, admin install)
installer\dist\Hangar-Setup-0.1.0.exe   (~70 MB, per-user install)
```
The Client installer requests admin (it injects input) and offers optional
sign-in autostart. The Master installs per-user with no UAC.

## Run it (Phase 0)

**1. Signaling server** — on a machine both ends can reach (localhost for a same-PC
test; a public VPS for real remote use):
```bash
cd server && npm install && npm start      # ws://0.0.0.0:8080
```

**2. Client** (on the PC to be controlled): install and run
`HangarAgent-Setup`. Set the server URL, click **Start**. It shows a
**9-digit ID** and a **password**. Hand those to whoever will connect.

**3. Master** (on your PC): install and run `Hangar-Setup`. Enter the
server URL, the Client's ID and password, click **Connect**. The remote screen
appears and your mouse + keyboard drive it.

> Same-PC smoke test: run all three locally with `ws://localhost:8080`.
> For real remote use, put the **server** on a public VPS and point both apps at it.
> Use `wss://` behind a TLS reverse proxy for anything beyond your own LAN.

**Release control:** in the Master, press **Ctrl+Alt+End** to disconnect without
sending those keys to the remote machine.

## Dev build (no installer)
```powershell
dotnet build RemoteDesktop.sln -c Release
dotnet run --project src\Client\Client.csproj
dotnet run --project src\Master\Master.csproj
```

## Safety / legality
Only connect to machines you own or are explicitly authorized to control. The Client
shows a visible "session active" banner and requires a password by design — keep both.
Screen capture + synthetic input + networking is also the classic RAT signature, so
antivirus may flag unsigned builds; code-sign the Client before distributing it.
See the security section in the architecture doc.
