using System.IO;
using System.Runtime.InteropServices;
using RemoteDesktop.Client;
using RemoteDesktop.Master;
using RemoteDesktop.Shared;

// Headless WebRTC end-to-end using the REAL production session classes, driven
// through the real Node signaling server. Proves: signaling -> offer/answer ->
// ICE/DTLS -> VP8 screen frames decoded on the master, and the input data channel
// opens. Input send is a no-op move to the current cursor position (no disruption).

int fails = 0;
void Check(bool ok, string msg) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {msg}"); if (!ok) fails++; }

const string SERVER = "ws://localhost:8080";
const string PASSWORD = "simpass";

string? assignedId = null;
var idReady = new TaskCompletionSource<string>();
var viewerConnected = new TaskCompletionSource<bool>();
int framesDecoded = 0;
int lastW = 0, lastH = 0;
var controlReady = new TaskCompletionSource<bool>();
List<RemoteMonitor>? monitors = null;
int currentMon = -99;
var monitorsReady = new TaskCompletionSource<bool>();

// --- host (client) ---
var host = new HostSession(SERVER, PASSWORD, fps: 12);
host.IdAssigned += id => { assignedId = id; idReady.TrySetResult(id); };
host.Status += s => Console.WriteLine($"[host] {s}");
await host.StartAsync();

var id2 = await WaitOr(idReady.Task, 10000);
Check(id2 is not null && id2.Length == 9, $"host registered, ID={id2}");
if (id2 is null) { Environment.Exit(Finish()); }

// --- master (viewer) ---
var viewer = new ViewerSession();
viewer.Connected += () => viewerConnected.TrySetResult(true);
viewer.Rejected += r => { Console.WriteLine($"[viewer] rejected: {r}"); viewerConnected.TrySetResult(false); };
viewer.Frame += (w, h, bgr) => { framesDecoded++; lastW = w; lastH = h; };
viewer.ControlReady += ready => { Console.WriteLine($"[viewer] control ready={ready}"); if (ready) controlReady.TrySetResult(true); };
viewer.Monitors += (m, cur, _) => { monitors = m; currentMon = cur; monitorsReady.TrySetResult(true); };
await viewer.ConnectAsync(SERVER, id2!, PASSWORD);

Check(await WaitBool(viewerConnected.Task, 10000), "viewer authenticated + paired");

// wait for the WebRTC pipeline to deliver decoded frames
var gotFrames = await WaitUntil(() => framesDecoded > 0, 20000);
Check(gotFrames, $"master decoded VP8 screen frames ({framesDecoded} frames, {lastW}x{lastH})");

// input data channel established
Check(await WaitBool(controlReady.Task, 20000), "input data channel opened (control ready)");

// exercise the input path without moving anything: send a move to current pos
Native.GetCursorPos(out var p);
double nx = lastW > 0 ? p.X / (double)lastW : 0.5;
double ny = lastH > 0 ? p.Y / (double)lastH : 0.5;
viewer.MouseMove(nx, ny);
Console.WriteLine("[viewer] sent no-op mouse move over data channel");
await Task.Delay(500);

// let a couple more frames flow to confirm sustained streaming
int before = framesDecoded;
await Task.Delay(1500);
Check(framesDecoded > before, $"sustained streaming ({framesDecoded - before} more frames in 1.5s)");

// multi-monitor: monitor list advertised
Check(await WaitBool(monitorsReady.Task, 5000) && monitors is { Count: >= 1 },
    $"host advertised monitor list ({monitors?.Count ?? 0} monitor(s), current={currentMon})");
if (monitors is { Count: >= 1 })
    foreach (var m in monitors)
        Console.WriteLine($"    monitor {m.Index}: {m.Width}x{m.Height}{(m.Primary ? " primary" : "")} '{m.Name}'");

// switch to "all monitors" (virtual desktop) and confirm streaming continues at new dims
int beforeSwitch = framesDecoded;
int wPre = lastW, hPre = lastH;
viewer.SelectMonitor(-1);
Console.WriteLine("[viewer] requested all-monitors (virtual desktop)");
bool switched = await WaitUntil(() => framesDecoded > beforeSwitch + 3, 8000);
Check(switched, $"streaming continued after monitor switch ({framesDecoded - beforeSwitch} frames, now {lastW}x{lastH}, was {wPre}x{hPre})");

// switch to a specific monitor by its enumeration Index and confirm we get that
// monitor's exact dimensions (verifies the Index-vs-list-position fix).
if (monitors is { Count: >= 1 })
{
    var target = monitors[^1];   // last in the (primary-first) list
    int b2 = framesDecoded;
    viewer.SelectMonitor(target.Index);
    Console.WriteLine($"[viewer] requested monitor Index={target.Index} ({target.Width}x{target.Height})");
    bool ok = await WaitUntil(() => framesDecoded > b2 + 3 && lastW == target.Width && lastH == target.Height, 8000);
    Check(ok, $"switched to specific monitor Index={target.Index}: got {lastW}x{lastH}, expected {target.Width}x{target.Height}");
}

// --- file transfer over the dedicated "file" data channel, both directions ---
var tmpDir = Path.Combine(Path.GetTempPath(), "ftd-e2e-files");
if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
Directory.CreateDirectory(tmpDir);
host.Files.SaveDirectory = Path.Combine(tmpDir, "host-rx");
viewer.Files.SaveDirectory = Path.Combine(tmpDir, "viewer-rx");

// 3 MB of noise: enough to exercise chunking + the ack window, quick to move locally.
var payload = new byte[3 * 1024 * 1024 + 12345];
new Random(42).NextBytes(payload);
var srcPath = Path.Combine(tmpDir, "blob.bin");
File.WriteAllBytes(srcPath, payload);

Check(await WaitUntil(() => viewer.Files.IsOpen, 10000), "file data channel opened");

var hostRx = new TaskCompletionSource<string>();
host.Files.ReceiveCompleted += (_, path) => hostRx.TrySetResult(path);
await viewer.Files.SendFileAsync(srcPath);
var hostSaved = await WaitOr(hostRx.Task, 20000);
Check(hostSaved is not null && File.ReadAllBytes(hostSaved).AsSpan().SequenceEqual(payload),
    $"file master->host transferred intact ({payload.Length} bytes -> {hostSaved})");

var viewerRx = new TaskCompletionSource<string>();
viewer.Files.ReceiveCompleted += (_, path) => viewerRx.TrySetResult(path);
await host.Files.SendFileAsync(srcPath);
var viewerSaved = await WaitOr(viewerRx.Task, 20000);
Check(viewerSaved is not null && File.ReadAllBytes(viewerSaved).AsSpan().SequenceEqual(payload),
    $"file host->master transferred intact ({payload.Length} bytes -> {viewerSaved})");

// name collision: sending the same file again must land as "blob (1).bin", not clobber
var hostRx2 = new TaskCompletionSource<string>();
host.Files.ReceiveCompleted += (_, path) => hostRx2.TrySetResult(path);
await viewer.Files.SendFileAsync(srcPath);
var second = await WaitOr(hostRx2.Task, 20000);
Check(second is not null && second != hostSaved && File.Exists(hostSaved),
    $"second send kept both copies ({Path.GetFileName(second ?? "?")})");

// --- remote browsing + pull: viewer lists a host directory, then downloads a file ---
var browseDir = Path.Combine(tmpDir, "remote-src");
Directory.CreateDirectory(browseDir);
var remoteFile = Path.Combine(browseDir, "pulled.bin");
var remoteBytes = new byte[1024 * 1024 + 77];
new Random(99).NextBytes(remoteBytes);
File.WriteAllBytes(remoteFile, remoteBytes);

FsListing? listing = null;
var listReady = new TaskCompletionSource<bool>();
void OnList(FsListing l) { listing = l; listReady.TrySetResult(true); }
viewer.Files.ListingReceived += OnList;
viewer.Files.RequestListing(browseDir);
await WaitBool(listReady.Task, 8000);
viewer.Files.ListingReceived -= OnList;
Check(listing is not null && listing.Entries.Any(en => en.Name == "pulled.bin" && !en.IsDir),
    $"remote listing returned the file ({listing?.Entries.Count ?? 0} entries)");

var pullDest = Path.Combine(tmpDir, "pulled-copy.bin");
var pulled = await viewer.Files.DownloadAsync(remoteFile, pullDest);
Check(File.Exists(pulled) && File.ReadAllBytes(pulled).AsSpan().SequenceEqual(remoteBytes),
    $"remote file pulled + intact ({remoteBytes.Length} bytes -> {pulled})");

try { Directory.Delete(tmpDir, recursive: true); } catch { }

// --- auto-update: manifest check + hash-verified download over the server's HTTP side ---
// Runs only when E2E_UPDATE_DIR is set (and the server was started with the same
// UPDATE_DIR), so a plain run without update wiring still passes.
var updDir = Environment.GetEnvironmentVariable("E2E_UPDATE_DIR");
if (!string.IsNullOrEmpty(updDir))
{
    Directory.CreateDirectory(updDir);
    var fakeExe = Path.Combine(updDir, "RemoteControl.exe");
    var exeBytes = new byte[512 * 1024];
    new Random(7).NextBytes(exeBytes);
    File.WriteAllBytes(fakeExe, exeBytes);
    string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exeBytes));
    var manifest = new
    {
        version = "9.9.9",
        notes = "e2e test build",
        master = new[] { new { name = "RemoteControl.exe", sha256 = sha, size = (long)exeBytes.Length } },
        client = new[] { new { name = "RemotlerAgent.exe", sha256 = sha, size = (long)exeBytes.Length } },
    };
    File.WriteAllText(Path.Combine(updDir, "manifest.json"),
        System.Text.Json.JsonSerializer.Serialize(manifest));

    var updater = new Updater("master", elevate: false);
    var info = await updater.CheckAsync(SERVER);
    Check(info is not null && info.Version == new Version(9, 9, 9), $"update check found newer version ({info?.Version})");
    if (info is not null)
    {
        var stage = await updater.DownloadAsync(SERVER, info);
        var got = Path.Combine(stage, "RemoteControl.exe");
        Check(File.Exists(got) && File.ReadAllBytes(got).AsSpan().SequenceEqual(exeBytes),
            "update downloaded + hash-verified");
        try { Directory.Delete(stage, true); } catch { }
    }

    // A manifest at/below the running version must not offer an update.
    File.WriteAllText(Path.Combine(updDir, "manifest.json"),
        System.Text.Json.JsonSerializer.Serialize(new
        {
            version = "0.0.1",
            master = new[] { new { name = "RemoteControl.exe", sha256 = sha, size = (long)exeBytes.Length } },
        }));
    Check(await new Updater("master", false).CheckAsync(SERVER) is null, "no update offered when manifest is older");
}

// --- admin directory: the online host shows up in /directory (auth) ---
// Gated on E2E_ADMIN_PASSWORD (must match the server's ADMIN_PASSWORD).
var adminPw = Environment.GetEnvironmentVariable("E2E_ADMIN_PASSWORD");
if (!string.IsNullOrEmpty(adminPw))
{
    var dir = await new DirectoryClient().FetchAsync(SERVER, adminPw);
    Check(dir.Clients.Any(c => c.Id == id2 && c.Online),
        $"directory lists the online host ({dir.Clients.Count} clients, {dir.Groups.Count} groups)");

    try { await new DirectoryClient().FetchAsync(SERVER, "definitely-wrong"); Check(false, "directory rejects a bad password"); }
    catch (UnauthorizedAccessException) { Check(true, "directory rejects a bad password"); }
    catch { Check(false, "directory rejects a bad password (unexpected error)"); }
}

await viewer.CloseAsync();

// --- admin (password-less) connect: a viewer proving the admin password to the relay
// is accepted by the host WITHOUT the per-client password (directory "connect") ---
if (!string.IsNullOrEmpty(adminPw))
{
    await Task.Delay(1000);   // let the host free up after the first viewer left
    var v2 = new ViewerSession();
    var v2conn = new TaskCompletionSource<bool>();
    v2.Connected += () => v2conn.TrySetResult(true);
    v2.Rejected += r => { Console.WriteLine($"[admin] rejected: {r}"); v2conn.TrySetResult(false); };
    await v2.ConnectAsync(SERVER, id2!, password: "", adminPassword: adminPw);
    Check(await WaitBool(v2conn.Task, 10000), "admin connect succeeds without the per-client password");
    await v2.CloseAsync();

    await Task.Delay(1000);
    var v3 = new ViewerSession();
    var v3conn = new TaskCompletionSource<bool>();
    v3.Connected += () => v3conn.TrySetResult(true);
    v3.Rejected += _ => v3conn.TrySetResult(false);
    await v3.ConnectAsync(SERVER, id2!, password: "", adminPassword: "wrong-admin-pw");
    Check(!await WaitBool(v3conn.Task, 6000), "wrong admin password does NOT get a password-less connect");
    await v3.CloseAsync();
}

await host.StopAsync();
Environment.Exit(Finish());

int Finish() { Console.WriteLine(); Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILURE(S)"); return fails; }

static async Task<string?> WaitOr(Task<string> t, int ms)
    => await Task.WhenAny(t, Task.Delay(ms)) == t ? t.Result : null;
static async Task<bool> WaitBool(Task<bool> t, int ms)
    => await Task.WhenAny(t, Task.Delay(ms)) == t && t.Result;
static async Task<bool> WaitUntil(Func<bool> cond, int ms)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(200); }
    return cond();
}

file static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
}
