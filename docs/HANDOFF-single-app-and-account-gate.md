# Handoff: one download, and an account requirement for relay connections

**Written against commit `1e6805c` on `master`.** Line numbers below are accurate at that
commit — re-check them if you read this later.

**Author's note:** this was written on macOS. WPF targets `net8.0-windows`, so none of it
could be compiled or run. Everything below is from reading the source; treat the code
shapes as direction, not as verified patches. Nothing here has been built.

---

## Why this document exists

Two asks came out of a review of the download page:

- **A.** Ship a single app instead of two separate downloads.
- **B.** Require an account for connections that go over the relay, leaving direct LAN
  connections anonymous.

While scoping A, it turned out most of it is already done. Read the next section before
planning any work — it changes the size of the job considerably.

---

## What is already true (read this first)

**`Remotler` is already a dual-role app.** It views *and* hosts.

- `src/Master/Master.csproj:34` — `<ProjectReference Include="..\Client\Client.csproj" />`
- `src/Master/MainWindow.xaml.cs:6` — `using RemoteDesktop.Client;   // HostSession (host engine)`
- `src/Master/MainWindow.xaml.cs:23,43` — holds a `HostSession?` and a `DirectHostListener?`
- `src/Master/MainWindow.xaml.cs:206` — calls `StartHosting()` on startup
- `src/Master/MainWindow.xaml.cs:66` — starts `DirectHostListener` on the LAN port

The reverse is not true: `src/Client` has no viewer. Nothing in it references
`ViewerSession`, `ViewerWindow` or `OpenViewer`.

So the two artifacts are **not** "the controlling half" and "the controlled half":

| Artifact | Assembly | Role |
|---|---|---|
| `Remotler-Setup-*.exe` | `RemoteControl.exe` | Full app — views **and** hosts, plus LAN direct listener |
| `RemotlerAgent-Setup-*.exe` | `RemotlerAgent.exe` + `RemotlerService.exe` | Host only, plus a Windows service for unattended access |

**The only capability the Agent has that Remotler lacks is the Windows service.**
`installer/client.iss:62-65` runs `sc.exe` under an `unattended` task to register
`RemotlerService`, which runs as LocalSystem and survives logoff and the login screen.
`installer/app.iss` has no service at all.

That service is a genuine capability, not duplication — but it is one optional component,
not a second application. **Change A is therefore a packaging change, not a 5,000-line
source merge.**

---

## Change A — one download

### The decision you have to make first

`src/Service/Supervisor.cs:33` hardcodes the worker binary:

```csharp
_workerExe = Path.Combine(AppContext.BaseDirectory, "RemotlerAgent.exe");
```

and launches it with `--worker` (`Supervisor.cs:70`) into the active console session via
`CreateProcessAsUser`. So the service depends on `RemotlerAgent.exe` specifically. Two ways
out, and they differ a lot in effort:

**Option 1 — one installer, two binaries (recommended).**
Keep `RemotlerAgent.exe` as the service worker. Ship it inside the Remotler installer
alongside `RemoteControl.exe`, and make the service an optional setup task. The user sees
one download; the disk layout keeps two exes. Nothing in the C# has to change.

**Option 2 — one installer, one binary.**
Teach `RemoteControl.exe` a `--worker` mode (headless host, no window) and repoint
`Supervisor._workerExe` at it. Genuinely one executable. Costs: a new startup path in
`src/Master/App.xaml.cs` that must not construct any window, and care that `SelfInstall`
(`src/Master/SelfInstall.cs`) does not relocate the exe out from under a running service —
it currently copies the exe to `%LocalAppData%\Programs\Remotler` and relaunches when run
from a "loose" location, which would be actively harmful under a LocalSystem service.

Option 1 gets the user-visible win for a fraction of the risk. Option 2 is the tidier end
state. Pick before starting.

### Steps for Option 1

1. `installer/app.iss` — add `RemotlerAgent.exe` and `RemotlerService.exe` to `[Files]`.
2. `installer/app.iss` — add an `unattended` task to `[Tasks]`, wording it as
   *"Install the background service for always-on unattended access"*, unchecked by
   default. Copy the `sc.exe create` / `sc.exe start` `[Run]` entries verbatim from
   `installer/client.iss:62-65`, and the matching delete in `[UninstallRun]`.
3. `installer/client.iss` — decide its fate. Keep it if you want a silent fleet-rollout
   artifact (`/VERYSILENT` with the service preselected); delete it if not. **If you keep
   it, this whole change buys nothing on the download page** — the point is one download.
4. `build.ps1` — it publishes both apps and compiles both installers (see its header
   comment, lines 1-13). Adjust to emit the single installer and stage the update package
   accordingly.
5. `manifest.json` — currently carries both `appInstaller` and `agentInstaller`. Decide
   whether `agentInstaller` disappears or stays for the fleet artifact. **This is a wire
   contract with three consumers, all of which break silently if it changes:**
   - `server/lib/releases.js` — resolves manifest and assets from GitHub Releases
   - `server/public/index.html` → `viewDownload()` — reads `appInstaller` / `agentInstaller`
   - `server/public/landing.html` — reads the same two keys via `[data-dl]` attributes
   - `src/Shared/Updater.cs` — the in-app updater
6. Update `README.md` — its "Build the installers" and "Run it" sections describe two
   installers, and lines 62-66 are already stale (they claim the app installs per-user with
   no UAC; `installer/app.iss:51` is `PrivilegesRequired=admin`).

The two web surfaces are cheap to change and are not blockers — say the word and they can
be updated on the server side independently.

---

## Change B — require an account for relay connections

### What happens today

Two connect paths, in `src/Master/MainWindow.xaml.cs`:

```
:240   OpenViewer(raw, raw, password: pw, authToken: null);        // typed ID + password
:247   OpenViewer(rid, nm, password: null, authToken: _acct?.Token); // picked from the fleet list
```

Line 240 is the anonymous path and it is the one most people use: type a 9-digit ID and a
password, connect. No account anywhere. `src/Master/ViewerSession.cs:57-65` sends
`{ t: "connect", id, auth }` only when `authToken` is non-empty.

Direct LAN is separate and already accountless by design —
`src/Client/DirectHostListener.cs` says so explicitly: *"no relay, no account, no
enrollment (TightVNC-style)"*, gated by the shared connection password.
`MainWindow.xaml.cs:87-99` (`TryParseDirect`) is what decides: an IP or hostname goes
direct, an all-digits string goes to the relay. **That split is exactly the line you want
to gate on, and it already exists.**

### Server side

The relay `connect` handler is `server/index.js`, `case 'connect':` around lines 570-600.
It already computes the viewer's identity:

```js
const auth = msg.auth || ws._cookieAuth;
const vuser = auth ? store.getUser(verifyToken(auth) || '') : null;
```

`vuser` is currently used only to record `ws._viewerEmail` for session history. The gate is
to make it mandatory — after the rate-limit check and before `hosts.get(...)`:

```js
if (!vuser) { send(ws, { t: 'rejected', reason: 'sign in to connect over the relay' }); break; }
```

That is the entire server change. It is small, and it is the dangerous part: **the moment
it deploys, every anonymous viewer stops working, including installed clients that have not
been updated.** Do not ship it before the client is out and adopted.

Note the reason string is surfaced to the user, so make it actionable.

### Client side

1. `MainWindow.xaml.cs:240` — before calling `OpenViewer` on the relay path, require
   `_acct?.Token`. If absent, prompt for sign-in rather than attempting and failing with a
   server rejection.
2. Leave `OpenDirectViewer` (`MainWindow.xaml.cs:103-107`) untouched. LAN stays anonymous.
3. Make the UI state honest: if the entered string parses as a relay ID and there is no
   account, say so before the user hits Connect.

### Migration — the part that needs a product decision

Existing users on v0.4.3 and earlier connect anonymously. Turning on the gate breaks them
with no warning. Options, roughly in order of kindness:

- Ship the client change first, wait for adoption via the in-app updater
  (`src/Shared/Updater.cs`), then enable the server gate.
- Put the gate behind an env var (e.g. `REQUIRE_ACCOUNT_FOR_RELAY`) so it can be switched
  on and rolled back without a deploy. Follows the existing config pattern in
  `server/index.js`.
- Soft-launch: log rejections without enforcing for a period, to see how many anonymous
  connects are actually happening before breaking them.

The middle option is cheap and worth doing regardless.

### Interaction with Change A

If you take Option 2 of Change A (one binary with a `--worker` mode), the headless worker
also has to hold an account token to register on the relay. Decide the ordering. Doing B
first against the current two-binary layout, then A, avoids doing the auth wiring twice.

---

## Verification

There is no cheap compile check on this repo from a non-Windows machine, and the CI path is
not a substitute:

- `.github/workflows/release.yml` runs on `windows-latest`, but triggers only on a `v*.*.*`
  tag push or `workflow_dispatch` — **both publish a public GitHub Release.** Using CI as a
  compile check means cutting a release per iteration.
- Worth adding: a `pull_request` / `push` trigger that runs `dotnet build` on
  `windows-latest` without the release steps. Cheap, and it makes this kind of change
  reviewable by anyone.

Manual checks after building:

1. Fresh install of the single installer, service task unchecked → app views and hosts.
2. Fresh install, service task checked → `sc query RemotlerService` reports RUNNING, and a
   host session survives sign-out.
3. Uninstall removes the service (`sc delete`) and the `Remotler Direct LAN` firewall rule
   (`installer/app.iss:78`).
4. LAN direct connect by IP still works with no account.
5. Relay connect with no account is refused with a readable message.
6. Relay connect with an account works.
7. In-app update from the previous version still finds and applies the new package.

---

## Open questions

1. Change A: Option 1 (one installer, two exes) or Option 2 (one true binary)?
2. Does `agentInstaller` survive in `manifest.json`, or does the fleet artifact go away?
3. Change B: does the *host* side also require an account to register on the relay, or only
   the viewer? Today an unenrolled host can get an ID and be reached with just a password.
   Gating only the viewer is the smaller change and probably the intent — confirm.
4. Is there a grace period for existing anonymous users, or is the cutover hard?

---

## Things the web side already handles

For context, so they are not re-done on the Windows side:

- `https://remotler.com/` reads `/update/manifest.json` at runtime and rewrites the download
  links and version labels, so the marketing page follows releases automatically.
- `/update/<file>` on the server 302s to the GitHub release asset
  (`server/lib/releases.js`), so nothing large is stored or proxied by the droplet.
- The dashboard Download tab reads the same manifest.

Change the manifest key names and all three break at once.
