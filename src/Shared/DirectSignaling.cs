using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Shared;

/// <summary>
/// Serverless signaling for LAN connections: a plain TCP link between two apps carrying
/// newline-delimited JSON control messages (connect / offer / answer / ICE). WebRTC media
/// then flows P2P over LAN host candidates — no relay involved. JSON strings never contain
/// raw newlines (SDP newlines are escaped as \n by the serializer), so lines frame cleanly.
/// </summary>
public sealed class DirectSignaling : ISignaling
{
    private static readonly byte[] Nl = { (byte)'\n' };
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    public event Action<JsonElement>? JsonReceived;
    public event Action<string?>? Closed;

    /// <summary>Viewer side: connect out to a host by IP/hostname.</summary>
    public async Task ConnectAsync(string host, int port)
    {
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, _cts.Token);
        _stream = _client.GetStream();
        _ = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Host side: take over an accepted connection and start reading.</summary>
    public void Attach(TcpClient client)
    {
        _client = client; _client.NoDelay = true;
        _stream = client.GetStream();
        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendJsonAsync(object obj)
    {
        var s = _stream;
        if (s == null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        await _sendLock.WaitAsync(_cts.Token);
        try { await s.WriteAsync(bytes, _cts.Token); await s.WriteAsync(Nl, _cts.Token); await s.FlushAsync(_cts.Token); }
        catch { /* peer gone; receive loop reports Closed */ }
        finally { try { _sendLock.Release(); } catch { } }
    }

    private async Task ReceiveLoopAsync()
    {
        string? reason = null;
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream!.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                int nl;
                while ((nl = FindNewline(sb)) >= 0)
                {
                    var line = sb.ToString(0, nl);
                    sb.Remove(0, nl + 1);
                    if (line.Length == 0) continue;
                    try { using var doc = JsonDocument.Parse(line); JsonReceived?.Invoke(doc.RootElement); }
                    catch { /* ignore malformed line */ }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reason = ex.Message; }
        finally { Closed?.Invoke(reason); }
    }

    private static int FindNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
        return -1;
    }

    public Task CloseAsync()
    {
        _cts.Cancel();
        try { _client?.Close(); } catch { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { _sendLock.Dispose(); } catch { }
    }
}
