using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Client;

/// <summary>
/// Client (host) side, Phase 2. The WebSocket carries only signaling
/// (register / password / SDP / ICE); the screen video and input travel over a
/// direct WebRTC peer connection:
///   video  host -> master  : VP8 on a media track
///   input  master -> host  : JSON on a "input" data channel
/// The host is the offerer (it owns the video), so it creates the data channel too.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HostSession : IDisposable
{
    private readonly string _serverUrl;
    private readonly string _password;
    private readonly int _fps;
    private readonly string? _token;       // stable identity for unattended hosts (null = random ID)
    private readonly string? _authToken;   // account session → claims this PC into the org
    private readonly string? _enrollToken; // pre-baked enrollment token → claims into an org (no login)
    private readonly string? _hostName;    // friendly computer name shown in the org list

    private ISignaling? _conn;
    private bool _direct;               // LAN direct-connect mode (no relay)
    private ScreenCapture? _capture;
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _inputChannel;
    private RTCDataChannel? _fileChannel;
    private RTCDataChannel? _thumbChannel;
    private ThumbnailStreamer? _thumbs;
    private WindowWatcher? _winWatch;
    private ClipboardMonitor? _clipMon;
    private VpxVideoEncoder? _encoder;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private readonly object _encLock = new();   // serialize encode vs. dispose (native codec)
    private volatile bool _pcConnected;

    private List<MonitorInfo> _monitors = new();
    private int _currentMon;                     // -1 = all monitors, else index into _monitors

    private readonly SystemMetrics _metrics = new();
    private System.Threading.Timer? _metricsTimer;   // periodic health report over signaling
    private List<RTCIceServer>? _iceServers;          // org network settings from 'registered'

    /// <summary>A sibling device in the same org+group, pushed by the relay for the "Your fleet" list.</summary>
    public sealed record FleetDevice(string Name, string? RelayId, string? GroupId, bool Online);

    public event Action<string>? IdAssigned;
    public event Action<List<FleetDevice>>? FleetUpdated;   // org/group siblings from the relay
    public event Action<string?>? OrgAssigned;   // org name this host is claimed into (null = none)
    public event Action<string?, string?>? GroupAssigned;   // group id, name (null = ungrouped)
    public event Action<string?, string?, string?>? Branding;   // appName, accent(#hex), logo(data URL)
    public event Action<string>? Status;
    public event Action<bool>? SessionActive;
    public event Action<HostSession>? Ended;   // direct-mode: the socket closed, listener can drop us

    /// <summary>File send/receive over the session's "file" data channel.</summary>
    public FileTransferChannel Files { get; } = new();

    public HostSession(string serverUrl, string password, int fps = 15, string? token = null,
                       string? authToken = null, string? hostName = null, string? enrollToken = null)
    {
        _serverUrl = serverUrl; _password = password; _fps = fps; _token = token;
        _authToken = authToken; _hostName = hostName; _enrollToken = enrollToken;
    }

    public async Task StartAsync()
    {
        _monitors = ScreenCapture.EnumerateMonitors();
        _currentMon = PrimaryIndex();
        _capture = new ScreenCapture(RegionFor(_currentMon));
        ApplyInputRegion();

        var ws = new SignalingConnection();
        _conn = ws;
        _conn.JsonReceived += OnJson;
        _conn.Closed += _ => { SessionActive?.Invoke(false); Status?.Invoke("Disconnected from server"); TearDownPeer(); };

        Status?.Invoke("Connecting to server…");
        await ws.ConnectAsync(_serverUrl);
        await _conn.SendJsonAsync(new { t = "register", token = _token, auth = _authToken, enroll = _enrollToken, name = _hostName, mac = PrimaryMac() });
    }

    /// <summary>
    /// LAN direct mode: serve a viewer that connected straight to us over TCP (no relay).
    /// The socket is already established; we wait for its {connect,password}, then offer.
    /// Uses host-candidate-only ICE (pure P2P on the local network).
    /// </summary>
    public async Task StartDirectAsync(ISignaling signaling)
    {
        _direct = true;
        _iceServers = new List<RTCIceServer>();   // LAN: host candidates only, no STUN/TURN
        _monitors = ScreenCapture.EnumerateMonitors();
        _currentMon = PrimaryIndex();
        _capture = new ScreenCapture(RegionFor(_currentMon));
        ApplyInputRegion();

        _conn = signaling;
        _conn.JsonReceived += OnJson;
        _conn.Closed += _ => { SessionActive?.Invoke(false); Status?.Invoke("Direct viewer disconnected"); TearDownPeer(); Ended?.Invoke(this); };
        Status?.Invoke("Direct LAN connection — waiting for viewer");
        await Task.CompletedTask;
    }

    private void OnJson(JsonElement root)
    {
        var t = root.TryGetProperty("t", out var tv) ? tv.GetString() : null;
        switch (t)
        {
            case "registered":
                IdAssigned?.Invoke(root.GetProperty("id").GetString() ?? "?");
                OrgAssigned?.Invoke(root.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.Object
                    ? o.GetProperty("name").GetString() : null);
                if (root.TryGetProperty("group", out var gp) && gp.ValueKind == JsonValueKind.Object)
                    GroupAssigned?.Invoke(gp.GetProperty("id").GetString(), gp.GetProperty("name").GetString());
                else GroupAssigned?.Invoke(null, null);
                if (root.TryGetProperty("ice", out var ice) && ice.ValueKind == JsonValueKind.Object)
                    _iceServers = IceConfig.FromJson(ice);
                if (root.TryGetProperty("branding", out var br) && br.ValueKind == JsonValueKind.Object)
                    Branding?.Invoke(
                        br.TryGetProperty("appName", out var an) ? an.GetString() : null,
                        br.TryGetProperty("accent", out var ac) ? ac.GetString() : null,
                        br.TryGetProperty("logo", out var lg) && lg.ValueKind == JsonValueKind.String ? lg.GetString() : null);
                Status?.Invoke("Ready — waiting for a connection");
                StartMetrics();
                break;

            case "connect":   // LAN direct mode: a viewer connected straight to us
            {
                if (!_direct) break;
                var pw = root.TryGetProperty("password", out var pv) ? pv.GetString() : "";
                if (string.Equals(pw, _password, StringComparison.Ordinal))
                {
                    _ = _conn!.SendJsonAsync(new { t = "connected", ice = (object?)null });
                    Status?.Invoke("Direct viewer connected — negotiating video…");
                    _ = StartPeerAsync();
                }
                else _ = _conn!.SendJsonAsync(new { t = "rejected", reason = "wrong password" });
                break;
            }

            case "connect-request":
            {
                var rid = root.GetProperty("rid").GetString();
                var pw = root.TryGetProperty("password", out var pv) ? pv.GetString() : "";
                // The relay sets admin=true when the viewer proved the admin password to
                // it; we trust the relay we chose to connect to, so accept without the
                // per-client password (this is the directory "connect without a prompt").
                bool admin = root.TryGetProperty("admin", out var av) && av.ValueKind == JsonValueKind.True;
                bool ok = admin || string.Equals(pw, _password, StringComparison.Ordinal);
                _ = _conn!.SendJsonAsync(new { t = "connect-response", rid, ok });
                if (ok) { _ = StartPeerAsync(); if (admin) Status?.Invoke("Session active (admin) — negotiating video…"); }
                else Status?.Invoke("Rejected a connection (wrong password)");
                break;
            }

            case "answer":
                _pc?.setRemoteDescription(new RTCSessionDescriptionInit
                    { type = RTCSdpType.answer, sdp = root.GetProperty("sdp").GetString() });
                break;

            case "ice":
                var cand = root.GetProperty("candidate").GetString();
                if (!string.IsNullOrEmpty(cand))
                    _pc?.addIceCandidate(new RTCIceCandidateInit { candidate = cand });
                break;

            case "selmon":
                SelectMonitor(root.GetProperty("index").GetInt32());
                break;

            case "fleet":
            {
                var list = new List<FleetDevice>();
                foreach (var d in root.GetProperty("list").EnumerateArray())
                    list.Add(new FleetDevice(
                        d.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                        d.TryGetProperty("relayId", out var ri) && ri.ValueKind == JsonValueKind.String ? ri.GetString() : null,
                        d.TryGetProperty("groupId", out var gi) && gi.ValueKind == JsonValueKind.String ? gi.GetString() : null,
                        d.TryGetProperty("online", out var on) && on.GetBoolean()));
                FleetUpdated?.Invoke(list);
                break;
            }

            case "cmd":
            {
                // Admin command over signaling (task manager, kill, WOL, config). Handled
                // synchronously here (args JsonElement is only valid during this callback).
                var reqId = root.GetProperty("reqId").GetString();
                var cmd = root.TryGetProperty("cmd", out var cv) ? cv.GetString() : null;
                var args = root.TryGetProperty("args", out var av) ? av : default;
                if (cmd == "config")
                {
                    // May run PowerShell/IO — extract now (args is only valid here), then run off-thread.
                    var spec = ConfigApply.Parse(args);
                    _ = Task.Run(async () =>
                    {
                        var r = ConfigApply.Run(spec);
                        r["t"] = "cmd-result"; r["reqId"] = reqId;
                        try { await _conn!.SendJsonAsync(r); } catch { }
                    });
                }
                else
                {
                    var reply = HostCommands.Handle(cmd, args);
                    reply["t"] = "cmd-result";
                    reply["reqId"] = reqId;
                    _ = _conn!.SendJsonAsync(reply);
                }
                break;
            }

            case "bye":
                SessionActive?.Invoke(false);
                Status?.Invoke("Viewer disconnected — waiting for a connection");
                TearDownPeer();
                break;
        }
    }

    private async Task StartPeerAsync()
    {
        SessionActive?.Invoke(true);
        Status?.Invoke("Session active — negotiating video…");

        _encoder = new VpxVideoEncoder();
        var config = new RTCConfiguration
        {
            iceServers = _iceServers ?? IceConfig.Default()
        };
        _pc = new RTCPeerConnection(config);

        var vp8 = new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 96, "VP8", 90000);
        _pc.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.video, false,
            new List<SDPAudioVideoMediaFormat> { vp8 }, MediaStreamStatusEnum.SendOnly));

        // Host creates the input channel; master sends input back over it.
        _inputChannel = await _pc.createDataChannel("input", null);
        _inputChannel.onmessage += (_, _, data) => InjectInput(data);

        // Second channel for file transfer, kept separate so a big transfer can
        // never stall input events behind it.
        _fileChannel = await _pc.createDataChannel("file", null);
        Files.Attach(_fileChannel);
        // Dropped files land in the folder the user has open in Explorer, else Downloads.
        Files.ResolveDropDir = () => ExplorerFolder.Foreground();

        // Low-fps per-monitor thumbnails for the viewer's rail + all-monitors overview.
        _thumbChannel = await _pc.createDataChannel("thumbs", null);
        _thumbs = new ThumbnailStreamer(_monitors, s => { try { if (_thumbChannel is { IsOpened: true }) _thumbChannel.send(s); } catch { } });

        // Flag a monitor the operator isn't watching when a new top-level window appears there.
        _winWatch = new WindowWatcher(_monitors, idx =>
        {
            if (idx == _currentMon) return;   // they're already looking at it
            try { if (_thumbChannel is { IsOpened: true }) _thumbChannel.send($"{{\"t\":\"newwin\",\"i\":{idx}}}"); } catch { }
        });

        // Remote→local clipboard: push text/files copied on this machine to the viewer.
        _clipMon = new ClipboardMonitor(
            text => { try { if (_thumbChannel is { IsOpened: true }) _thumbChannel.send(JsonSerializer.Serialize(new { t = "clip", text })); } catch { } },
            files =>
            {
                try
                {
                    var arr = files.Select(f => { long sz = 0; try { sz = new FileInfo(f).Length; } catch { } return new { path = f, name = Path.GetFileName(f), size = sz }; }).ToArray();
                    if (arr.Length > 0 && _thumbChannel is { IsOpened: true }) _thumbChannel.send(JsonSerializer.Serialize(new { t = "clipfiles", files = arr }));
                }
                catch { }
            });

        _pc.onicecandidate += c =>
        {
            if (c != null) _ = _conn!.SendJsonAsync(new { t = "ice", candidate = c.ToString() });
        };
        _pc.onconnectionstatechange += s =>
        {
            _pcConnected = s == RTCPeerConnectionState.connected;
            if (s == RTCPeerConnectionState.connected)
            {
                Status?.Invoke("Session active — streaming");
                SendMonitorList();
                StartPump();
                _thumbs?.Start();
                _winWatch?.Start();
                _clipMon?.Start();
            }
            else if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected or RTCPeerConnectionState.closed)
                StopPump();
        };

        var offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer);
        await _conn!.SendJsonAsync(new { t = "offer", sdp = offer.sdp });
    }

    // MAC of the primary up, non-loopback adapter — reported so the org can Wake-on-LAN it.
    private static string? PrimaryMac()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var mac = ni.GetPhysicalAddress().ToString();
                if (mac.Length == 12) return mac;
            }
        }
        catch { }
        return null;
    }

    private int PrimaryIndex()
    {
        foreach (var m in _monitors) if (m.Primary) return m.Index;
        return _monitors.Count > 0 ? _monitors[0].Index : 0;
    }

    // index -1 = whole virtual desktop (all monitors); else a monitor by its
    // enumeration Index (NOT list position — the list is sorted primary-first).
    private System.Drawing.Rectangle RegionFor(int index)
    {
        if (index < 0 || _monitors.Count == 0) return ScreenCapture.VirtualDesktop();
        foreach (var m in _monitors) if (m.Index == index) return m.Bounds;
        return _monitors[0].Bounds;
    }

    private void ApplyInputRegion()
    {
        var r = _capture!.Region;
        InputInjector.SetRegion(r.X, r.Y, r.Width, r.Height);
    }

    private void SendMonitorList()
    {
        var list = _monitors.Select(m => new { i = m.Index, x = m.X, y = m.Y, w = m.Width, h = m.Height, primary = m.Primary, name = m.Name });
        var vd = ScreenCapture.VirtualDesktop();   // combined-surface bounds for Show-All chips/dividers
        _ = _conn!.SendJsonAsync(new { t = "monitors", list, current = _currentMon, vd = new { x = vd.X, y = vd.Y, w = vd.Width, h = vd.Height } });
    }

    private void SelectMonitor(int index)
    {
        // Re-enumerate so a rearranged/added/removed display (or resolution change) is picked
        // up — important for Show-All (index -1 = the whole, possibly-changed virtual desktop).
        _monitors = ScreenCapture.EnumerateMonitors();
        _currentMon = index;
        var region = RegionFor(index);
        // Hold the encoder lock so the pump can't encode against a half-changed region
        // or a disposed codec; recreate the codec so the next frame is a keyframe at the
        // new dimensions.
        lock (_encLock)
        {
            _capture!.SetRegion(region);
            _encoder?.Dispose();
            _encoder = new VpxVideoEncoder();
        }
        ApplyInputRegion();
        SendMonitorList();   // push refreshed bounds + virtual-desktop geometry to the viewer
    }

    private void StartPump()
    {
        if (_pumpCts != null) return;               // already running
        _pumpCts = new CancellationTokenSource();
        var token = _pumpCts.Token;
        uint duration = (uint)(90000 / _fps);       // VP8 90 kHz clock
        _pumpTask = Task.Run(async () =>
        {
            int delay = Math.Max(1, 1000 / _fps);
            while (!token.IsCancellationRequested && _pcConnected)
            {
                try
                {
                    byte[] bgr = _capture!.CaptureBgr();
                    byte[]? enc;
                    // Hold the lock across the native encode so teardown can't free
                    // the codec mid-call (would be an uncatchable AccessViolation).
                    lock (_encLock)
                    {
                        if (_encoder == null || token.IsCancellationRequested) break;
                        enc = _encoder.EncodeVideo(_capture.Width, _capture.Height, bgr,
                            VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8);
                    }
                    if (enc is { Length: > 0 }) _pc?.SendVideo(duration, enc);
                }
                catch { /* transient send error; keep going */ }
                try { await Task.Delay(delay, token); } catch { break; }
            }
        }, token);
    }

    private void StopPump()
    {
        _pumpCts?.Cancel();
        try { _pumpTask?.Wait(2000); } catch { /* ignore aggregate/cancel */ }
        _pumpTask = null;
        _pumpCts = null;
    }

    private void InjectInput(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(data));
            var r = doc.RootElement;
            switch (r.GetProperty("t").GetString())
            {
                case "m": InputInjector.MouseMove(r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble()); break;
                case "b": InputInjector.MouseButton(r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble(),
                              r.GetProperty("btn").GetInt32(), r.GetProperty("down").GetBoolean()); break;
                case "w": InputInjector.MouseWheel(r.GetProperty("dy").GetInt32()); break;
                case "k": InputInjector.KeyVirtual(r.GetProperty("vk").GetInt32(), r.GetProperty("down").GetBoolean()); break;
                case "ping":
                    // Echo the probe back over the thumbs channel so the viewer can measure RTT.
                    try { if (_thumbChannel is { IsOpened: true }) _thumbChannel.send($"{{\"t\":\"pong\",\"ts\":{r.GetProperty("ts").GetInt64()}}}"); } catch { }
                    break;
                case "clip":
                {
                    var text = r.GetProperty("text").GetString() ?? "";
                    ClipboardHelper.SetText(text);
                    _clipMon?.NoteText(text);   // don't echo it back to the viewer
                    break;
                }
            }
        }
        catch { /* ignore malformed input packet */ }
    }

    // Report host health (CPU/mem/disk) over signaling every 5s while registered, so the
    // dashboard shows live metrics even when no viewer is connected.
    private void StartMetrics()
    {
        _metricsTimer ??= new System.Threading.Timer(_ =>
        {
            try
            {
                if (_conn == null) return;
                var (cpu, mem, disk) = _metrics.Sample();
                _ = _conn.SendJsonAsync(new { t = "metrics", cpu, mem, disk });
            }
            catch { /* transient; try again next tick */ }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private void StopMetrics()
    {
        _metricsTimer?.Dispose();
        _metricsTimer = null;
    }

    private void TearDownPeer()
    {
        _pcConnected = false;
        StopPump();                                 // guarantees the pump loop has exited
        _thumbs?.Dispose(); _thumbs = null;
        _winWatch?.Dispose(); _winWatch = null;
        _clipMon?.Dispose(); _clipMon = null;
        Files.Detach();
        try { _fileChannel?.close(); } catch { }
        try { _inputChannel?.close(); } catch { }
        try { _thumbChannel?.close(); } catch { }
        try { _pc?.Close("session ended"); } catch { }
        _inputChannel = null; _fileChannel = null; _thumbChannel = null; _pc = null;
        lock (_encLock) { _encoder?.Dispose(); _encoder = null; }
    }

    public async Task StopAsync()
    {
        StopMetrics();
        TearDownPeer();
        if (_conn != null) await _conn.CloseAsync();
    }

    public void Dispose()
    {
        StopMetrics();
        TearDownPeer();
        _conn?.Dispose();
        _capture?.Dispose();
    }
}
