using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using FFmpeg.AutoGen;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.FFmpeg;
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
    private volatile int _fps;                   // mutable: quality profiles change it live
    private volatile int _h264Mbps = 6;          // per-profile H.264 target (maxrate/bufsize scale with it)
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
    private FFmpegVideoEncoder? _h264;                 // non-null only when H.264 was negotiated + available
    private VideoCodecsEnum _sendCodec = VideoCodecsEnum.VP8;   // set by OnVideoFormatsNegotiated
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

    public HostSession(string serverUrl, string password, int fps = 30, string? token = null,
                       string? authToken = null, string? hostName = null, string? enrollToken = null)
    {
        _serverUrl = serverUrl; _password = password; _fps = fps; _token = token;
        _authToken = authToken; _hostName = hostName; _enrollToken = enrollToken;
    }

    /// <summary>Ask the relay to re-send this host's org/group fleet (address-book refresh).</summary>
    public void RequestFleet()
    {
        try { _ = _conn?.SendJsonAsync(new { t = "fleet-req" }); } catch { }
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
        await _conn.SendJsonAsync(new { t = "register", token = _token, auth = _authToken, enroll = _enrollToken, name = _hostName, mac = PrimaryMac(),
                                        ver = typeof(HostSession).Assembly.GetName().Version?.ToString(3) });
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
                // The relay sends a freshly-resolved ICE set with each request — the copy
                // from registration can hold expired short-lived TURN credentials.
                if (root.TryGetProperty("ice", out var cice) && cice.ValueKind == JsonValueKind.Object)
                    _iceServers = IceConfig.FromJson(cice);
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

            case "profile":   // viewer picked a connection quality profile
                ApplyProfile(root.TryGetProperty("name", out var prof) ? prof.GetString() : null);
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
        // A new viewer can be authorized while a previous session's peer still exists
        // (its viewer vanished without a bye and the relay replaced it). Start clean —
        // two live peer connections sharing this host's pump/channel state is chaos.
        if (_pc != null) TearDownPeer();

        SessionActive?.Invoke(true);
        Status?.Invoke("Session active — negotiating video…");

        _encoder = new VpxVideoEncoder();
        _sendCodec = VideoCodecsEnum.VP8;

        // Offer H.264 (preferred) + VP8 when FFmpeg is available; the peer's answer decides which
        // is used, and OnVideoFormatsNegotiated tells us which to encode. If FFmpeg or H.264
        // isn't available on either side, negotiation falls back to VP8 — no behaviour change.
        bool h264 = FFmpegSupport.H264EncodeAvailable;
        if (h264)
        {
            try { _h264 = MakeH264Encoder(); }
            catch { _h264 = null; h264 = false; }
        }

        var config = new RTCConfiguration
        {
            iceServers = _iceServers ?? IceConfig.Default()
        };
        _pc = new RTCPeerConnection(config);

        var formats = new List<SDPAudioVideoMediaFormat>();
        if (h264) formats.Add(new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 100, "H264", 90000));
        formats.Add(new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 96, "VP8", 90000));
        _pc.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.video, false, formats, MediaStreamStatusEnum.SendOnly));

        _pc.OnVideoFormatsNegotiated += fmts =>
        {
            if (fmts != null && fmts.Count > 0) _sendCodec = fmts[0].Codec;
        };

        // Host creates the input channel; master sends input back over it.
        _inputChannel = await _pc.createDataChannel("input", null);
        _inputChannel.onmessage += (_, _, data) => InjectInput(data);

        // Second channel for file transfer, kept separate so a big transfer can
        // never stall input events behind it.
        _fileChannel = await _pc.createDataChannel("file", null);
        Files.Attach(_fileChannel);
        // Dropped files land in the folder the user has open in Explorer, else Downloads.
        Files.ResolveDropDir = () => ExplorerFolder.Foreground();
        // Viewer clicked the folder icon on a pushed file — open it in Explorer on this machine.
        Files.RevealRequested += RevealInExplorer;

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
        var pcRef = _pc;
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
            {
                // Only act on events from the CURRENT session. A replaced/torn-down peer
                // fires failed/closed long after a new session has started — reacting to
                // it here (especially StopPump) would strangle the new session's stream.
                if (!ReferenceEquals(pcRef, _pc)) return;
                StopPump();
                // A dead link must free this host even when no 'bye' ever arrives
                // (viewer crashed / network died) — otherwise the UI stays on
                // "streaming" and the next connect is rejected as busy.
                if (s == RTCPeerConnectionState.failed)
                {
                    Status?.Invoke("Viewer connection lost — waiting for a connection");
                    _ = Task.Run(TearDownPeer);   // off this callback: Close() re-enters state changes
                }
            }
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
            // Recreate the H.264 encoder too so it re-initialises at the new dimensions and
            // emits a fresh keyframe.
            if (_h264 != null) { try { _h264.Dispose(); _h264 = MakeH264Encoder(); } catch { _h264 = null; } }
        }
        ApplyInputRegion();
        SendMonitorList();   // push refreshed bounds + virtual-desktop geometry to the viewer
    }

    private void StartPump()
    {
        if (_pumpCts != null) return;               // already running
        _pumpCts = new CancellationTokenSource();
        var token = _pumpCts.Token;
        _pumpTask = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            byte[]? prev = null;                 // last sent frame — GDI-path change detection only
            long lastSentMs = -1;
            int sent = 0;                        // diag: breadcrumb after the first sent frame
            bool notedErr = false;               // diag: note only the first pre-frame error
            while (!token.IsCancellationRequested && _pcConnected)
            {
                long t0 = sw.ElapsedMilliseconds;
                try
                {
                    byte[] frame = _capture!.CaptureBgra(out bool? dxChanged);
                    // Skip unchanged frames — a large CPU + bandwidth win on static screens —
                    // but still send at least ~1/s so a recovering decoder always has a recent
                    // frame. DXGI reports change exactly from the duplication metadata; only
                    // the GDI fallback needs the memcmp diff.
                    bool changed;
                    if (dxChanged.HasValue) { changed = dxChanged.Value; prev = null; }
                    else changed = prev == null || prev.Length != frame.Length
                                   || !frame.AsSpan().SequenceEqual(prev);
                    if (changed || t0 - lastSentMs >= 1000)
                    {
                        byte[]? enc;
                        // Hold the lock across the native encode so teardown can't free
                        // the codec mid-call (would be an uncatchable AccessViolation).
                        lock (_encLock)
                        {
                            if (token.IsCancellationRequested) break;
                            // Region switched between capture and encode → the buffer no longer
                            // matches _capture dimensions; encoding it would over-read. Drop it.
                            if (frame.Length != _capture.Width * _capture.Height * 4)
                                enc = null;
                            else if (_sendCodec == VideoCodecsEnum.H264 && _h264 != null)
                                enc = _h264.EncodeVideo(_capture.Width, _capture.Height, frame,
                                    VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.H264);
                            else if (_encoder != null)
                                enc = _encoder.EncodeVideo(_capture.Width, _capture.Height, frame,
                                    VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);
                            else break;
                        }
                        if (enc is { Length: > 0 })
                        {
                            // RTP timestamps advance by real elapsed time (90 kHz clock), not a
                            // fixed per-frame constant — skipped frames and slow captures would
                            // otherwise skew the receiver's jitter buffer.
                            long now = sw.ElapsedMilliseconds;
                            uint duration = lastSentMs < 0
                                ? (uint)(90000 / _fps)
                                : (uint)Math.Clamp((now - lastSentMs) * 90, 90, 270000);
                            _pc?.SendVideo(duration, enc);
                            lastSentMs = now;
                            if (++sent == 1)
                                CrashLog.Note($"pump: first frame sent {_capture.Width}x{_capture.Height} codec={_sendCodec} backend={_capture.Backend} h264={FFmpegSupport.H264EncoderName ?? "no"}");
                        }
                        if (changed && !dxChanged.HasValue)
                        {
                            if (prev == null || prev.Length != frame.Length) prev = new byte[frame.Length];
                            Buffer.BlockCopy(frame, 0, prev, 0, frame.Length);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!notedErr) { notedErr = true; CrashLog.Note("pump: error: " + ex.Message); }
                    /* transient; keep going */
                }
                // Pace to the target fps (re-read each tick — profiles change it live),
                // subtracting the work just done so we add as little latency as possible.
                int interval = Math.Max(1, 1000 / _fps);
                int sleep = (int)Math.Max(1, interval - (sw.ElapsedMilliseconds - t0));
                try { await Task.Delay(sleep, token); } catch { break; }
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

    // Open a received file in Explorer on this (host) machine, selecting it if it exists.
    // Note: under the unattended service (SYSTEM, session 0) this cannot surface on the
    // interactive desktop; it works for the attended host running as the logged-in user.
    private static void RevealInExplorer(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (System.IO.Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // Quality profiles the viewer can switch live (over signaling): fps cap + H.264 target
    // rate. VP8 has no exposed rate control in SIPSorcery, so profiles reach it via fps only.
    private void ApplyProfile(string? name)
    {
        (int fps, int mbps) = name switch
        {
            "responsive" => (30, 3),
            "quality"    => (15, 12),
            "framerate"  => (60, 6),
            "ultra"      => (60, 16),
            _            => (30, 6),      // "balanced" — matches the defaults
        };
        _fps = fps; _h264Mbps = mbps;
        // Recreate the encoders so the new rate/GOP takes effect and the next frame is a
        // keyframe (same pattern as a monitor switch).
        lock (_encLock)
        {
            _encoder?.Dispose(); _encoder = new VpxVideoEncoder();
            if (_h264 != null) { try { _h264.Dispose(); _h264 = MakeH264Encoder(); } catch { _h264 = null; } }
        }
        CrashLog.Note($"profile: {name ?? "balanced"} fps={fps} h264={mbps}M");
    }

    // Build an H.264 encoder bound to the hardware codec probed at startup (NVENC/QSV/AMF),
    // tuned for interactive desktop streaming: capped CBR-ish rate control, no B-frames or
    // reordering delay, ~4 s GOP. Without options the FFmpeg defaults are file-transcoding
    // oriented (unbounded quality-based rate, frame reordering) — bad latency and bursts.
    private FFmpegVideoEncoder MakeH264Encoder()
    {
        var enc = new FFmpegVideoEncoder();
        var name = FFmpegSupport.H264EncoderName;
        if (!string.IsNullOrEmpty(name))
        {
            int mbps = _h264Mbps, max = mbps + Math.Max(1, mbps / 3);
            // The options dictionary alone does NOT set the rate: measured, 3/6/12/16 Mbps
            // all produced byte-identical output. SetBitrate writes the codec context
            // fields directly and must be called BEFORE SetCodec opens the context.
            long bps = mbps * 1_000_000L;
            try { enc.SetBitrate(bps, null, null, max * 1_000_000L); } catch { }
            var opts = new Dictionary<string, string>
            {
                ["b"] = $"{mbps}M", ["maxrate"] = $"{max}M", ["bufsize"] = $"{max}M",
                ["g"] = Math.Max(30, _fps * 4).ToString(),              // keyframe every ~4 s
                ["bf"] = "0",                                           // no B-frames (reordering = latency)
            };
            switch (name)
            {
                case "h264_nvenc":
                    opts["preset"] = "p3"; opts["tune"] = "ull"; opts["rc"] = "cbr";
                    opts["delay"] = "0"; opts["zerolatency"] = "1";
                    break;
                case "h264_qsv":
                    opts["preset"] = "veryfast"; opts["async_depth"] = "1";
                    break;
                case "h264_amf":
                    opts["usage"] = "ultralowlatency"; opts["quality"] = "speed"; opts["rc"] = "cbr";
                    break;
                case "libopenh264":
                    opts["rc_mode"] = "bitrate";
                    break;
            }
            if (!enc.SetCodec(AVCodecID.AV_CODEC_ID_H264, name, opts))
            {
                // A driver that rejects an option shouldn't cost us hardware H.264 —
                // fall back to the encoder's defaults rather than to software VP8.
                enc.Dispose();
                enc = new FFmpegVideoEncoder();
                try { enc.SetBitrate(bps, null, null, max * 1_000_000L); } catch { }
                enc.SetCodec(AVCodecID.AV_CODEC_ID_H264, name, null);
            }
        }
        return enc;
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
        lock (_encLock) { _encoder?.Dispose(); _encoder = null; _h264?.Dispose(); _h264 = null; }
        _sendCodec = VideoCodecsEnum.VP8;
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
