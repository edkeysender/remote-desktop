using System.IO;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace RemoteDesktop.Client;

/// <summary>
/// One-time FFmpeg init + H.264 availability probe. The native FFmpeg 8 libraries
/// (avcodec-*/avutil-*/…) must sit next to the exe (or in an "ffmpeg" subfolder). If they
/// aren't present or the H.264 codec can't be used, <see cref="H264Available"/> is false and
/// the app negotiates plain VP8 — so the whole feature degrades gracefully to today's path.
/// </summary>
public static class FFmpegSupport
{
    private static bool? _available;
    private static readonly object _gate = new();

    /// <summary>True if FFmpeg loaded and an H.264 frame could actually be encoded.</summary>
    public static bool H264Available
    {
        get
        {
            lock (_gate)
            {
                _available ??= Probe();
                return _available.Value;
            }
        }
    }

    private static bool Probe()
    {
        var libDir = FindLibDir();
        if (libDir == null) return false;
        try
        {
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_FATAL, libDir);
            // Confirm the H.264 encoder is really usable (the codec may be absent from a
            // given FFmpeg build) by encoding a few tiny frames until one is produced.
            using var probe = new FFmpegVideoEncoder();
            var black = new byte[64 * 64 * 3];
            for (int i = 0; i < 5; i++)
            {
                var enc = probe.EncodeVideo(64, 64, black, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264);
                if (enc is { Length: > 0 }) return true;
            }
            return false;
        }
        catch { return false; }
    }

    // Where the native FFmpeg DLLs live: an "ffmpeg" subfolder next to the exe, then the
    // exe folder itself. Detected by the presence of an avcodec-*.dll.
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
