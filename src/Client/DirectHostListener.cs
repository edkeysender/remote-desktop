using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Client;

/// <summary>
/// Listens on a LAN TCP port so viewers can connect straight to this machine by IP —
/// no relay, no account, no enrollment (TightVNC-style). Each accepted viewer gets its own
/// direct-mode <see cref="HostSession"/>; the shared connection password gates access.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DirectHostListener : IDisposable
{
    public const int DefaultPort = 8891;

    private readonly int _port;
    private readonly Func<string> _password;   // current connection password (read fresh each connect)
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private readonly List<HostSession> _sessions = new();

    public DirectHostListener(int port, Func<string> password) { _port = port; _password = password; }

    public void Start()
    {
        if (_listener != null) return;
        _cts = new CancellationTokenSource();
        try { _listener = new TcpListener(IPAddress.Any, _port); _listener.Start(); }
        catch { _listener = null; return; }   // port in use / blocked — direct mode just unavailable
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var sig = new DirectSignaling();
        var host = new HostSession("", _password());
        host.Ended += h => { lock (_lock) _sessions.Remove(h); try { h.Dispose(); } catch { } };
        lock (_lock) _sessions.Add(host);
        try
        {
            await host.StartDirectAsync(sig);   // wires handlers first…
            sig.Attach(client);                  // …then start reading (no missed messages)
        }
        catch { try { client.Close(); } catch { } lock (_lock) _sessions.Remove(host); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        lock (_lock) { foreach (var h in _sessions) try { h.Dispose(); } catch { } _sessions.Clear(); }
    }
}
