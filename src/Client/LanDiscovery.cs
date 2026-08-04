using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Client;

/// <summary>A device announcing itself on the local network.</summary>
public sealed record LanDevice(string Id, string Name, string Org, string GroupId, string GroupName);

/// <summary>
/// Peer discovery on the LAN: each running app broadcasts a small UDP announcement
/// (id/name/org/group) every few seconds and listens for others. Lets the app show
/// same-network fleet devices without the server enumerating them. Best-effort — if the
/// port can't bind (another app using it) discovery is simply disabled.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    private const int Port = 47821;
    private UdpClient? _sock;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _tx;
    private string _self = "";
    private byte[] _payload = Array.Empty<byte>();
    private readonly ConcurrentDictionary<string, (LanDevice dev, long ts)> _peers = new();

    public event Action? Changed;

    public void Start(string selfId, string name, string org, string groupId, string groupName)
    {
        UpdatePayload(selfId, name, org, groupId, groupName);
        if (_self == selfId && _sock != null) return;   // already running for this identity
        Stop();
        _self = selfId;
        try
        {
            _sock = new UdpClient();
            _sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _sock.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            _sock.EnableBroadcast = true;
            _cts = new CancellationTokenSource();
            _ = ReceiveLoop(_cts.Token);
            _tx = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(4));
        }
        catch { _sock = null; /* discovery unavailable */ }
    }

    private void UpdatePayload(string id, string name, string org, string gid, string gname) =>
        _payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { t = "remotler-announce", id, name, org, groupId = gid, groupName = gname }));

    private void Tick()
    {
        try { _sock?.Send(_payload, _payload.Length, new IPEndPoint(IPAddress.Broadcast, Port)); } catch { }
        Changed?.Invoke();   // also drives periodic UI refresh (prunes stale peers)
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _sock != null)
        {
            try
            {
                var r = await _sock.ReceiveAsync(ct);
                using var doc = JsonDocument.Parse(r.Buffer);
                var e = doc.RootElement;
                if (!(e.TryGetProperty("t", out var t) && t.GetString() == "remotler-announce")) continue;
                var id = e.GetProperty("id").GetString() ?? "";
                if (id.Length == 0 || id == _self) continue;
                var dev = new LanDevice(id,
                    e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    e.TryGetProperty("org", out var o) ? o.GetString() ?? "" : "",
                    e.TryGetProperty("groupId", out var gi) ? gi.GetString() ?? "" : "",
                    e.TryGetProperty("groupName", out var gn) ? gn.GetString() ?? "" : "");
                bool isNew = !_peers.ContainsKey(id);
                _peers[id] = (dev, Environment.TickCount64);
                if (isNew) Changed?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(200, ct); } catch { break; } }
        }
    }

    /// <summary>Live peers seen in the last 15s (prunes stale entries).</summary>
    public List<LanDevice> GetDevices()
    {
        var now = Environment.TickCount64;
        var live = new List<LanDevice>();
        foreach (var kv in _peers)
        {
            if (now - kv.Value.ts < 15000) live.Add(kv.Value.dev);
            else _peers.TryRemove(kv.Key, out _);
        }
        return live;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _tx?.Dispose(); } catch { }
        try { _sock?.Dispose(); } catch { }
        _tx = null; _sock = null; _cts = null;
    }

    public void Dispose() => Stop();
}
