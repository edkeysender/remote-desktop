using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Shared;

/// <summary>
/// Thin wrapper over <see cref="ClientWebSocket"/> shared by client and master.
/// Handles the connect/receive loop, splits text (JSON control/input) from binary
/// (media frames), and serializes outbound sends behind a lock.
///
/// Events fire on the receive-loop thread; UI consumers should marshal to their
/// dispatcher. This class knows nothing about WPF.
/// </summary>
public sealed class SignalingConnection : ISignaling
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>A JSON text message arrived. The element is valid only during the callback.</summary>
    public event Action<JsonElement>? JsonReceived;
    /// <summary>A binary frame arrived (JPEG in Phase 0).</summary>
    public event Action<byte[]>? BinaryReceived;
    /// <summary>The socket closed (reason may be null).</summary>
    public event Action<string?>? Closed;

    public WebSocketState State => _ws.State;

    public async Task ConnectAsync(string url)
    {
        await _ws.ConnectAsync(new Uri(url), _cts.Token);
        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendJsonAsync(object obj)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        await SendRawAsync(bytes, WebSocketMessageType.Text);
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data)
        => SendRawAsync(data, WebSocketMessageType.Binary);

    private async Task SendRawAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type)
    {
        if (_ws.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(_cts.Token);
        try { await _ws.SendAsync(data, type, true, _cts.Token); }
        catch { /* peer gone; receive loop reports Closed */ }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[128 * 1024];
        var textSb = new StringBuilder();
        using var binMs = new MemoryStream();
        string? reason = null;
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) { reason = result.CloseStatusDescription; break; }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    binMs.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) { BinaryReceived?.Invoke(binMs.ToArray()); binMs.SetLength(0); }
                    continue;
                }

                textSb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;
                var text = textSb.ToString();
                textSb.Clear();
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    JsonReceived?.Invoke(doc.RootElement);
                }
                catch { /* ignore malformed */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reason = ex.Message; }
        finally { Closed?.Invoke(reason); }
    }

    public async Task CloseAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        _cts.Cancel();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _ws.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
