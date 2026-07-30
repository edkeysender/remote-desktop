using System.Runtime.InteropServices;
using RemoteDesktop.Client;
using RemoteDesktop.Master;

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

await viewer.CloseAsync();
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
