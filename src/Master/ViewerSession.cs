using System.Text.Json;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

/// <summary>
/// Drives the master (viewer) side: connect to the signaling server, request a
/// session by ID+password, receive JPEG frames, and forward input events.
/// UI-agnostic; the WPF window subscribes to events and calls the Send* methods.
/// </summary>
public sealed class ViewerSession : IDisposable
{
    private readonly SignalingConnection _conn = new();

    public event Action? Connected;
    public event Action<string>? Rejected;      // reason
    public event Action<int, int>? ScreenSize;  // w, h
    public event Action<byte[]>? Frame;         // JPEG bytes
    public event Action<string?>? Closed;

    public async Task ConnectAsync(string serverUrl, string id, string password)
    {
        _conn.JsonReceived += OnJson;
        _conn.BinaryReceived += data => Frame?.Invoke(data);
        _conn.Closed += reason => Closed?.Invoke(reason);
        await _conn.ConnectAsync(serverUrl);
        await _conn.SendJsonAsync(new { t = "connect", id, password });
    }

    private void OnJson(JsonElement root)
    {
        var t = root.TryGetProperty("t", out var tv) ? tv.GetString() : null;
        switch (t)
        {
            case "connected": Connected?.Invoke(); break;
            case "rejected":  Rejected?.Invoke(root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""); break;
            case "screen":    ScreenSize?.Invoke(root.GetProperty("w").GetInt32(), root.GetProperty("h").GetInt32()); break;
            case "bye":       Closed?.Invoke(root.TryGetProperty("reason", out var b) ? b.GetString() : null); break;
        }
    }

    // ---- outbound input (coords normalized 0..1 of the source image) ----
    public void MouseMove(double nx, double ny) => _ = _conn.SendJsonAsync(new { t = "m", x = nx, y = ny });
    public void MouseButton(double nx, double ny, int btn, bool down) => _ = _conn.SendJsonAsync(new { t = "b", x = nx, y = ny, btn, down });
    public void MouseWheel(int dy) => _ = _conn.SendJsonAsync(new { t = "w", dy });
    public void KeyVirtual(int vk, bool down) => _ = _conn.SendJsonAsync(new { t = "k", vk, down });

    public async Task CloseAsync() { try { await _conn.SendJsonAsync(new { t = "bye" }); } catch { } await _conn.CloseAsync(); }
    public void Dispose() => _conn.Dispose();
}
