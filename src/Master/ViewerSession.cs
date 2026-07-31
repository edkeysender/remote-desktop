using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

/// <summary>A monitor the host is offering, as advertised over signaling.</summary>
public readonly record struct RemoteMonitor(int Index, int Width, int Height, bool Primary, string Name);

/// <summary>
/// Master (viewer) side, Phase 2. The WebSocket carries only signaling; video and
/// input travel over a direct WebRTC peer connection. The master is the answerer:
/// it receives the host's VP8 track (decodes to BGR) and receives the host-created
/// "input" data channel (sends input JSON back over it).
/// </summary>
public sealed class ViewerSession : IDisposable
{
    private readonly SignalingConnection _conn = new();
    private readonly VpxVideoEncoder _decoder = new();
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _inputChannel;
    private int _controlReadyFired;   // 0/1 guard so ControlReady fires once

    public event Action? Connected;
    public event Action<string>? Rejected;
    public event Action<int, int, byte[]>? Frame;   // width, height, BGR24
    public event Action<bool>? ControlReady;        // input data channel open/closed
    public event Action<List<RemoteMonitor>, int>? Monitors;  // available monitors, current index
    public event Action<string?>? Closed;

    /// <summary>File send/receive over the session's "file" data channel.</summary>
    public FileTransferChannel Files { get; } = new();

    public async Task ConnectAsync(string serverUrl, string id, string password)
    {
        _conn.JsonReceived += OnJson;
        _conn.Closed += reason => Closed?.Invoke(reason);
        await _conn.ConnectAsync(serverUrl);
        await _conn.SendJsonAsync(new { t = "connect", id, password });
    }

    private void OnJson(JsonElement root)
    {
        var t = root.TryGetProperty("t", out var tv) ? tv.GetString() : null;
        switch (t)
        {
            case "connected":
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
                        m.GetProperty("i").GetInt32(), m.GetProperty("w").GetInt32(),
                        m.GetProperty("h").GetInt32(), m.GetProperty("primary").GetBoolean(),
                        m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
                int current = root.TryGetProperty("current", out var c) ? c.GetInt32() : 0;
                Monitors?.Invoke(mons, current);
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
            iceServers = new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } }
        };
        _pc = new RTCPeerConnection(config);

        var vp8 = new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 96, "VP8", 90000);
        _pc.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.video, false,
            new List<SDPAudioVideoMediaFormat> { vp8 }, MediaStreamStatusEnum.RecvOnly));

        _pc.ondatachannel += chan =>
        {
            // The host opens two channels: "input" for control, "file" for transfers.
            if (chan.label == "file") { Files.Attach(chan); return; }
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

        _pc.OnVideoFrameReceived += (_, _, encoded, _) =>
        {
            try
            {
                foreach (var s in _decoder.DecodeVideo(encoded, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8))
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
        _conn.Dispose();
    }
}
