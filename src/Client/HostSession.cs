using System.Runtime.Versioning;
using System.Text.Json;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Client;

/// <summary>
/// Drives the client (host) side: connect to the signaling server, obtain an ID,
/// verify a viewer's password locally, then stream JPEG frames and replay input.
/// UI-agnostic; raises events the WPF window subscribes to.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HostSession : IDisposable
{
    private readonly string _serverUrl;
    private readonly string _password;
    private readonly int _fps;
    private readonly long _quality;

    private SignalingConnection? _conn;
    private ScreenCapture? _capture;
    private CancellationTokenSource? _pumpCts;
    private volatile bool _streaming;

    public event Action<string>? IdAssigned;      // 9-digit ID
    public event Action<string>? Status;          // human-readable status line
    public event Action<bool>? SessionActive;     // true = a viewer is connected

    public HostSession(string serverUrl, string password, int fps = 12, long quality = 50)
    {
        _serverUrl = serverUrl; _password = password; _fps = fps; _quality = quality;
    }

    public async Task StartAsync()
    {
        _capture = new ScreenCapture(_quality);
        _conn = new SignalingConnection();
        _conn.JsonReceived += OnJson;
        _conn.Closed += reason =>
        {
            _streaming = false;
            SessionActive?.Invoke(false);
            Status?.Invoke($"Disconnected from server ({reason ?? "closed"})");
        };

        Status?.Invoke("Connecting to server…");
        await _conn.ConnectAsync(_serverUrl);
        await _conn.SendJsonAsync(new { t = "register" });
        StartPump();
    }

    private void OnJson(JsonElement root)
    {
        var t = root.TryGetProperty("t", out var tv) ? tv.GetString() : null;
        switch (t)
        {
            case "registered":
                IdAssigned?.Invoke(root.GetProperty("id").GetString() ?? "?");
                Status?.Invoke("Ready — waiting for a connection");
                break;

            case "connect-request":
            {
                var rid = root.GetProperty("rid").GetString();
                var pw = root.TryGetProperty("password", out var pv) ? pv.GetString() : "";
                bool ok = string.Equals(pw, _password, StringComparison.Ordinal);
                // Phase 1: replace with Argon2id verify + on-screen "Allow?" prompt.
                _ = _conn!.SendJsonAsync(new { t = "connect-response", rid, ok });
                if (ok)
                {
                    _streaming = true;
                    SessionActive?.Invoke(true);
                    Status?.Invoke("Session active — a viewer is connected");
                    _ = _conn.SendJsonAsync(new { t = "screen", w = _capture!.Width, h = _capture.Height });
                }
                else Status?.Invoke("Rejected a connection (wrong password)");
                break;
            }

            case "bye":
                _streaming = false;
                SessionActive?.Invoke(false);
                Status?.Invoke("Viewer disconnected — waiting for a connection");
                break;

            // ---- input from the viewer ----
            case "m": InputInjector.MouseMove(GetD(root, "x"), GetD(root, "y")); break;
            case "b": InputInjector.MouseButton(GetD(root, "x"), GetD(root, "y"),
                          root.GetProperty("btn").GetInt32(), root.GetProperty("down").GetBoolean()); break;
            case "w": InputInjector.MouseWheel(root.GetProperty("dy").GetInt32()); break;
            case "k":
                if (root.TryGetProperty("vk", out var vk))
                    InputInjector.KeyVirtual(vk.GetInt32(), root.GetProperty("down").GetBoolean());
                else if (root.TryGetProperty("code", out var code))
                    InputInjector.KeyCode(code.GetString() ?? "", root.GetProperty("down").GetBoolean());
                break;
        }
    }

    private void StartPump()
    {
        _pumpCts = new CancellationTokenSource();
        var token = _pumpCts.Token;
        _ = Task.Run(async () =>
        {
            int delay = Math.Max(1, 1000 / _fps);
            while (!token.IsCancellationRequested)
            {
                if (_streaming && _conn!.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    try
                    {
                        byte[] jpeg = _capture!.CaptureJpeg();
                        await _conn.SendBinaryAsync(jpeg);
                    }
                    catch { await Task.Delay(200, token); }
                }
                try { await Task.Delay(delay, token); } catch { break; }
            }
        }, token);
    }

    private static double GetD(JsonElement e, string name) => e.GetProperty(name).GetDouble();

    public async Task StopAsync()
    {
        _pumpCts?.Cancel();
        if (_conn != null) await _conn.CloseAsync();
    }

    public void Dispose()
    {
        _pumpCts?.Cancel();
        _conn?.Dispose();
        _capture?.Dispose();
    }
}
