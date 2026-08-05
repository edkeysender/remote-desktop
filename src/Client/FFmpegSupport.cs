using System.IO;
using FFmpeg.AutoGen;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace RemoteDesktop.Client;

/// <summary>
/// One-time FFmpeg init and H.264 capability probing. We ship LGPL FFmpeg (no GPL libx264),
/// so H.264 <b>encoding</b> is available only through a hardware encoder (NVENC / QuickSync /
/// AMF); H.264 <b>decoding</b> uses FFmpeg's native decoder and is available whenever the libs
/// load. If nothing is available the app negotiates plain VP8 — no behaviour change.
///
/// The native FFmpeg 8 DLLs (avcodec-*/avutil-*/swscale-*/…) must sit next to the exe, or in an
/// "ffmpeg" subfolder.
/// </summary>
public static class FFmpegSupport
{
    private static readonly object _gate = new();
    private static bool _init;
    private static bool _libsOk;             // FFmpeg loaded → H.264 DECODE available
    private static bool _encProbed;
    private static bool _encOk;              // a hardware H.264 encoder actually works
    private static string? _encName;         // e.g. "h264_nvenc"

    // Hardware H.264 encoders, best first. libx264 is deliberately absent (GPL, not shipped).
    private static readonly string[] HwEncoders = { "h264_nvenc", "h264_qsv", "h264_amf" };

    /// <summary>H.264 decode is possible (FFmpeg libs loaded). Used by the viewer.</summary>
    public static bool H264DecodeAvailable { get { EnsureInit(); return _libsOk; } }

    /// <summary>A hardware H.264 encoder works on this machine. Used by the host.</summary>
    public static bool H264EncodeAvailable { get { ProbeEncoder(); return _encOk; } }

    /// <summary>The FFmpeg encoder name to use for H.264 (null if none). Host side.</summary>
    public static string? H264EncoderName { get { ProbeEncoder(); return _encName; } }

    private static void EnsureInit()
    {
        lock (_gate)
        {
            if (_init) return;
            _init = true;
            var libDir = FindLibDir();
            if (libDir == null) return;
            try { FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_FATAL, libDir); _libsOk = true; }
            catch { _libsOk = false; }
        }
    }

    private static void ProbeEncoder()
    {
        lock (_gate)   // Monitor is reentrant, so calling EnsureInit() here is fine
        {
            if (_encProbed) return;
            EnsureInit();
            _encProbed = true;
            if (!_libsOk) return;
            var black = new byte[64 * 64 * 3];
            foreach (var name in HwEncoders)
            {
                try
                {
                    using var enc = new FFmpegVideoEncoder();
                    enc.SetCodec(AVCodecID.AV_CODEC_ID_H264, name, null);
                    for (int i = 0; i < 5; i++)
                    {
                        var e = enc.EncodeVideo(64, 64, black, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264);
                        if (e is { Length: > 0 }) { _encOk = true; _encName = name; return; }
                    }
                }
                catch { /* encoder not present/usable — try the next */ }
            }
        }
    }

    // Where the native FFmpeg DLLs live: an "ffmpeg" subfolder next to the exe, then the exe
    // folder itself. Detected by the presence of an avcodec-*.dll.
    private static string? FindLibDir()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var d in new[] { Path.Combine(baseDir, "ffmpeg"), baseDir })
        {
            try { if (Directory.Exists(d) && Directory.GetFiles(d, "avcodec-*.dll").Length > 0) return d; }
            catch { /* ignore and try next */ }
        }
        return null;
    }
}
