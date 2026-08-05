using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.FFmpeg;
using RemoteDesktop.Client;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

/// <summary>A monitor the host is offering, as advertised over signaling (bounds in virtual-desktop coords).</summary>
public readonly record struct RemoteMonitor(int Index, int X, int Y, int Width, int Height, bool Primary, string Name);

/// <summary>The host's whole virtual desktop (combined surface bounds) for Show-All.</summary>
public readonly record struct VdBounds(int X, int Y, int W, int H);

/// <summary>A file copied on the remote clipboard (to be pulled to the local machine).</summary>
public readonly record struct RemoteClipFile(string Path, string Name, long Size);

/// <summary>
/// Master (viewer) side, Phase 2. The WebSocket carries only signaling; video and
/// input travel over a direct WebRTC peer connection. The master is the answerer:
/// it receives the host's VP8 track (decodes to BGR) and receives the host-created
/// "input" data channel (sends input JSON back over it).
/// </summary>
public sealed class ViewerSession : IDisposable
{
    private ISignaling _conn = new SignalingConnection();
    private readonly VpxVideoEncoder _decoder = new();
    private FFmpegVideoEncoder? _h264dec;               // created on demand when H.264 is available
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _inputChannel;
    private int _controlReadyFired;   // 0/1 guard so ControlReady fires once
    private List<RTCIceServer>? _iceServers;   // org network settings from 'connected'

    public event Action? Connected;
    public event Action<string>? Rejected;
    public event Action<int, int, byte[]>? Frame;   // width, height, BGR24
    public event Action<bool>? ControlReady;        // input data channel open/closed
    public event Action<List<RemoteMonitor>, int, VdBounds>? Monitors;  // monitors, current index, virtual-desktop bounds
    public event Action<int, int, int, byte[]>? Thumbnail;    // monitor index, w, h, JPEG bytes
    public event Action<int>? NewWindow;                      // a new top-level window appeared on this monitor
    public event Action<long>? PongReceived;                  // echo of a ping timestamp (for RTT)
    public event Action<string>? ClipboardText;               // text copied on the remote → set locally
    public event Action<List<RemoteClipFile>>? ClipboardFiles;// files copied on the remote → pull + set locally
    public event Action<string?>? Closed;

    /// <summary>File send/receive over the session's "file" data channel.</summary>
    public FileTransferChannel Files { get; } = new();

    /// <summary>
    /// Join a host by ID. Three auth modes, in priority order:
    ///   • <paramref name="authToken"/> — an account session; the relay authorizes by
    ///     org + group membership and the host connects without a per-client password.
    ///   • <paramref name="adminPassword"/> — the relay's admin password (legacy picker).
    ///   • <paramref name="password"/> — the host's own password (manual connect).
    /// </summary>
    public async Task ConnectAsync(string serverUrl, string id, string password,
                                   string? adminPassword = null, string? authToken = null, string? peerToken = null)
    {
        _conn.JsonReceived += OnJson;
        _conn.Closed += reason => Closed?.Invoke(reason);
        await ((SignalingConnection)_conn).ConnectAsync(serverUrl);
        // `from` = this device's own host token; lets the relay authorize a password-less
        // connect to a sibling in the same org+group even when no user account is signed in.
        if (!string.IsNullOrEmpty(authToken))
            await _conn.SendJsonAsync(new { t = "connect", id, auth = authToken, from = peerToken });
        else if (!string.IsNullOrEmpty(adminPassword))
            await _conn.SendJsonAsync(new { t = "connect", id, admin = true, adminPassword, from = peerToken });
        else
            await _conn.SendJsonAsync(new { t = "connect", id, password, from = peerToken });
    }

    /// <summary>LAN direct connect: dial a host by IP/hostname, no relay. Host-candidate ICE only.</summary>
    public async Task ConnectDirectAsync(string host, int port, string password, string? peerToken = null)
    {
        _iceServers = new List<RTCIceServer>();   // LAN: host candidates only
        var d = new DirectSignaling();
        _conn = d;
        _conn.JsonReceived += OnJson;
        _conn.Closed += reason => Closed?.Invoke(reason);
        await d.ConnectAsync(host, port);
        await _conn.SendJsonAsync(new { t = "connect", password, from = peerToken });
    }

    private void OnJson(JsonElement root)
    {
        var t = root.TryGetProperty("t", out var tv) ? tv.GetString() : null;
        switch (t)
        {
            case "connected":
                if (root.TryGetProperty("ice", out var ice) && ice.ValueKind == JsonValueKind.Object)
                    _iceServers = IceConfig.FromJson(ice);
                Connected?.Invoke();
                SetupPeer();
                break;

            case "rejected":
                Rejected?.Invoke(root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "");
                break;

            case "offer":
                _ = HandleOfferAsync(root.GetProperty("sdp").GetString() ?? "");
                break;

            case "ice":
                var cand = root.GetProperty("candidate").GetString();
                if (!string.IsNullOrEmpty(cand))
                    _pc?.addIceCandidate(new RTCIceCandidateInit { candidate = cand });
                break;

            case "monitors":
            {
                var mons = new List<RemoteMonitor>();
                foreach (var m in root.GetProperty("list").EnumerateArray())
                    mons.Add(new RemoteMonitor(
                        m.GetProperty("i").GetInt32(),
                        m.TryGetProperty("x", out var mx) ? mx.GetInt32() : 0,
                        m.TryGetProperty("y", out var my) ? my.GetInt32() : 0,
                        m.GetProperty("w").GetInt32(), m.GetProperty("h").GetInt32(),
                        m.GetProperty("primary").GetBoolean(),
                        m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
                int current = root.TryGetProperty("current", out var c) ? c.GetInt32() : 0;
                var vd = new VdBounds(0, 0, 0, 0);
                if (root.TryGetProperty("vd", out var v) && v.ValueKind == JsonValueKind.Object)
                    vd = new VdBounds(v.GetProperty("x").GetInt32(), v.GetProperty("y").GetInt32(),
                                      v.GetProperty("w").GetInt32(), v.GetProperty("h").GetInt32());
                Monitors?.Invoke(mons, current, vd);
                break;
            }

            case "bye":
                Closed?.Invoke(root.TryGetProperty("reason", out var b) ? b.GetString() : null);
                break;
        }
    }

    private void SetupPeer()
    {
        var config = new RTCConfiguration
        {
            iceServers = _iceServers ?? IceConfig.Default()
        };
        _pc = new RTCPeerConnection(config);

        // Accept H.264 (preferred) + VP8 when FFmpeg is available for decode; otherwise VP8 only,
        // so a viewer without the FFmpeg libs simply negotiates VP8 with the host.
        var formats = new List<SDPAudioVideoMediaFormat>();
        if (FFmpegSupport.H264DecodeAvailable)
        {
            try { _h264dec = new FFmpegVideoEncoder(); formats.Add(new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 100, "H264", 90000)); }
            catch { _h264dec = null; }
        }
        formats.Add(new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 96, "VP8", 90000));
        _pc.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.video, false, formats, MediaStreamStatusEnum.RecvOnly));

        _pc.ondatachannel += chan =>
        {
            // The host opens: "input" for control, "file" for transfers, "thumbs" for the rail.
            if (chan.label == "file") { Files.Attach(chan); return; }
            if (chan.label == "thumbs") { chan.onmessage += (_, _, data) => OnThumb(data); return; }
            _inputChannel = chan;
            // On the answerer, the received channel's onopen does not reliably fire
            // (SIPSorcery), so poll IsOpened to detect readiness. Keep onopen too.
            chan.onopen += MarkControlReady;
            chan.onclose += () => ControlReady?.Invoke(false);
            _ = WatchChannelOpenAsync(chan);
        };

        _pc.onicecandidate += c =>
        {
            if (c != null) _ = _conn.SendJsonAsync(new { t = "ice", candidate = c.ToString() });
        };

        _pc.OnVideoFrameReceived += (_, _, encoded, format) =>
        {
            try
            {
                // Decode with the codec the frame actually arrived in (negotiated per session).
                var samples = format.Codec == VideoCodecsEnum.H264 && _h264dec != null
                    ? _h264dec.DecodeVideo(encoded, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264)
                    : _decoder.DecodeVideo(encoded, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8);
                foreach (var s in samples)
                    Frame?.Invoke((int)s.Width, (int)s.Height, s.Sample);
            }
            catch { /* skip a bad frame */ }
        };
    }

    private async Task HandleOfferAsync(string sdp)
    {
        if (_pc == null) return;
        _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);
        await _conn.SendJsonAsync(new { t = "answer", sdp = answer.sdp });
    }

    private void OnThumb(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var r = doc.RootElement;
            var kind = r.TryGetProperty("t", out var tt) ? tt.GetString() : "thumb";
            if (kind == "newwin") { NewWindow?.Invoke(r.GetProperty("i").GetInt32()); return; }
            if (kind == "pong") { PongReceived?.Invoke(r.GetProperty("ts").GetInt64()); return; }
            if (kind == "clip") { ClipboardText?.Invoke(r.GetProperty("text").GetString() ?? ""); return; }
            if (kind == "clipfiles")
            {
                var list = new List<RemoteClipFile>();
                foreach (var f in r.GetProperty("files").EnumerateArray())
                    list.Add(new RemoteClipFile(
                        f.GetProperty("path").GetString() ?? "",
                        f.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                        f.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
                if (list.Count > 0) ClipboardFiles?.Invoke(list);
                return;
            }
            if (kind != "thumb") return;
            int i = r.GetProperty("i").GetInt32(), w = r.GetProperty("w").GetInt32(), h = r.GetProperty("h").GetInt32();
            var bytes = Convert.FromBase64String(r.GetProperty("img").GetString() ?? "");
            if (bytes.Length > 0) Thumbnail?.Invoke(i, w, h, bytes);
        }
        catch { /* ignore a malformed thumbnail frame */ }
    }

    private void MarkControlReady()
    {
        if (Interlocked.Exchange(ref _controlReadyFired, 1) == 0) ControlReady?.Invoke(true);
    }

    private async Task WatchChannelOpenAsync(RTCDataChannel chan)
    {
        for (int i = 0; i < 40 && _controlReadyFired == 0; i++)
        {
            if (chan.IsOpened) { MarkControlReady(); return; }
            await Task.Delay(250);
        }
    }

    // ---- outbound input over the data channel (coords normalized 0..1) ----
    private void Send(object o)
    {
        if (_inputChannel is { IsOpened: true })
            _inputChannel.send(JsonSerializer.Serialize(o));
    }

    public void MouseMove(double nx, double ny) => Send(new { t = "m", x = nx, y = ny });
    public void MouseButton(double nx, double ny, int btn, bool down) => Send(new { t = "b", x = nx, y = ny, btn, down });
    public void MouseWheel(int dy) => Send(new { t = "w", dy });
    public void KeyVirtual(int vk, bool down) => Send(new { t = "k", vk, down });

    /// <summary>Send a latency probe; the host echoes it back over the "thumbs" channel as a pong.</summary>
    public void Ping(long ts) => Send(new { t = "ping", ts });
    /// <summary>Push the operator's clipboard text to the remote machine.</summary>
    public void SendClipboard(string text) => Send(new { t = "clip", text });

    /// <summary>Ask the host to switch the captured monitor (-1 = all). Sent over signaling.</summary>
    public void SelectMonitor(int index) => _ = _conn.SendJsonAsync(new { t = "selmon", index });

    public async Task CloseAsync()
    {
        Files.Detach();
        try { await _conn.SendJsonAsync(new { t = "bye" }); } catch { }
        try { _pc?.Close("closed by user"); } catch { }
        await _conn.CloseAsync();
    }

    public void Dispose()
    {
        Files.Detach();
        try { _pc?.Close("disposed"); } catch { }
        try { _h264dec?.Dispose(); } catch { }
        _conn.Dispose();
    }
}
