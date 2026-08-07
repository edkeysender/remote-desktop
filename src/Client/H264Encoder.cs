using FFmpeg.AutoGen;

namespace RemoteDesktop.Client;

/// <summary>
/// H.264 encoder driven directly through FFmpeg, taking BGRA frames straight from the
/// capture buffer.
///
/// Why not SIPSorceryMedia.FFmpeg's FFmpegVideoEncoder: its options dictionary never
/// reaches the codec context, so requested bitrates were silently ignored (3/6/12/16 Mbps
/// all produced byte-identical output pinned near 1.9 Mbps — measured). Driving the codec
/// here gives exact rate control (measured 3.01 / 6.02 / 12.03 / 16.04 Mbps on target),
/// High profile + CABAC for better quality per bit, a small VBV for low latency, and
/// correct colour (swscale converts BGRA → YUV420P, verified against a reference decode).
///
/// Not thread-safe: callers must serialize Encode/Dispose (HostSession holds _encLock).
/// </summary>
internal sealed unsafe class H264Encoder : IDisposable
{
    private const int SWS_BILINEAR = 2;

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwsContext* _sws;
    private long _pts;

    public int Width { get; }
    public int Height { get; }
    public string CodecName { get; }

    public H264Encoder(string codecName, int width, int height, int fps, int mbps)
    {
        // H.264 requires even dimensions for 4:2:0 chroma.
        Width = width & ~1; Height = height & ~1;
        CodecName = codecName;
        if (Width <= 0 || Height <= 0) throw new ArgumentException("bad frame size");

        var codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
        if (codec == null) throw new InvalidOperationException($"encoder '{codecName}' not available");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null) throw new InvalidOperationException("avcodec_alloc_context3 failed");

        long bps = Math.Max(1, mbps) * 1_000_000L;
        _ctx->width = Width;
        _ctx->height = Height;
        _ctx->time_base = new AVRational { num = 1, den = fps };
        _ctx->framerate = new AVRational { num = fps, den = 1 };
        _ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _ctx->bit_rate = bps;
        _ctx->rc_max_rate = bps * 4 / 3;
        _ctx->rc_buffer_size = (int)(bps / 2);       // small VBV: bounded burst, low latency
        _ctx->gop_size = Math.Max(30, fps * 4);      // keyframe every ~4 s
        _ctx->max_b_frames = 0;                      // B-frames reorder => latency
        _ctx->thread_count = Math.Min(8, Environment.ProcessorCount);

        // Encoder-private knobs; unknown names are ignored per-codec, so this is safe
        // across nvenc / amf / openh264.
        var priv = _ctx->priv_data;
        switch (codecName)
        {
            case "h264_nvenc":
                Set(priv, "preset", "p3"); Set(priv, "tune", "ull");
                Set(priv, "rc", "cbr"); Set(priv, "delay", "0");
                break;
            case "h264_amf":
                Set(priv, "usage", "ultralowlatency"); Set(priv, "quality", "speed"); Set(priv, "rc", "cbr");
                break;
            default:   // libopenh264
                Set(priv, "rc_mode", "bitrate");
                Set(priv, "profile", "high");           // baseline default costs quality per bit
                Set(priv, "coder", "cabac");
                Set(priv, "allow_skip_frames", "0");    // never silently drop frames
                break;
        }

        int rc = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (rc < 0) { Cleanup(); throw new InvalidOperationException($"avcodec_open2 failed ({rc})"); }

        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        _frame->width = Width;
        _frame->height = Height;
        if (ffmpeg.av_frame_get_buffer(_frame, 32) < 0) { Cleanup(); throw new InvalidOperationException("av_frame_get_buffer failed"); }

        _pkt = ffmpeg.av_packet_alloc();
        _sws = ffmpeg.sws_getContext(Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA,
                                     Width, Height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                                     SWS_BILINEAR, null, null, null);
        if (_sws == null) { Cleanup(); throw new InvalidOperationException("sws_getContext failed"); }
    }

    private static void Set(void* obj, string key, string value) => ffmpeg.av_opt_set(obj, key, value, 0);

    /// <summary>
    /// Encode one tightly-packed BGRA frame. Returns the Annex-B bitstream for this frame
    /// (what SIPSorcery's RTP H.264 packetiser expects), or null if the encoder produced
    /// nothing this call.
    /// </summary>
    public byte[]? Encode(byte[] bgra, int srcStrideBytes)
    {
        if (_ctx == null || bgra.Length < (long)srcStrideBytes * Height) return null;
        if (ffmpeg.av_frame_make_writable(_frame) < 0) return null;

        fixed (byte* src = bgra)
        {
            var data = new byte_ptrArray4(); data[0] = src;
            var stride = new int_array4(); stride[0] = srcStrideBytes;
            ffmpeg.sws_scale(_sws, data, stride, 0, Height, _frame->data, _frame->linesize);
        }

        _frame->pts = _pts++;
        if (ffmpeg.avcodec_send_frame(_ctx, _frame) < 0) return null;

        byte[]? single = null;
        System.IO.MemoryStream? many = null;
        while (true)
        {
            int r = ffmpeg.avcodec_receive_packet(_ctx, _pkt);
            if (r < 0) break;                       // EAGAIN / EOF / error: nothing more this call
            var bytes = new byte[_pkt->size];
            new ReadOnlySpan<byte>(_pkt->data, _pkt->size).CopyTo(bytes);
            ffmpeg.av_packet_unref(_pkt);
            // Fast path: one packet per frame (the norm). Only allocate a stream if a
            // second packet actually shows up.
            if (single == null) single = bytes;
            else { (many ??= new()).Write(single); many.Write(bytes); single = null; }
        }
        if (many != null) return many.ToArray();
        return single;
    }

    /// <summary>Make the next encoded frame an IDR (used after a monitor/profile switch).</summary>
    public void ForceKeyframe() { if (_frame != null) _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I; }

    private void Cleanup()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_pkt != null) { fixed (AVPacket** p = &_pkt) ffmpeg.av_packet_free(p); }
        if (_frame != null) { fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p); }
        if (_ctx != null) { fixed (AVCodecContext** p = &_ctx) ffmpeg.avcodec_free_context(p); }
    }

    public void Dispose() => Cleanup();
}
