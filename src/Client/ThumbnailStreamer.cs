using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Streams a low-fps downscaled JPEG thumbnail of every monitor to the viewer over the
/// "thumbs" data channel, so the viewer's monitor rail and all-monitors overview can show
/// live miniatures of each display. Cheap by construction: the screen is scaled directly
/// into the 192-px thumbnail during the blit (no full-resolution intermediate copy), and a
/// monitor whose thumbnail bytes didn't change since the last tick sends nothing.
/// One message per monitor: {"t":"thumb","i":idx,"w":w,"h":h,"img":"&lt;base64 jpeg&gt;"}.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThumbnailStreamer : IDisposable
{
    private const int ThumbWidth = 192;   // matches the rail's expanded thumbnail (retina-ish)
    private const int IntervalMs = 2000;
    private readonly List<MonitorInfo> _mons;
    private readonly Action<string> _send;
    private readonly ImageCodecInfo _jpeg;
    private readonly EncoderParameters _jpegParams;
    private readonly Dictionary<int, Bitmap> _thumbs = new();
    private readonly Dictionary<int, byte[]> _lastJpeg = new();   // per-monitor skip-if-unchanged
    private System.Threading.Timer? _timer;
    private int _busy;   // 0/1 guard so a slow tick can't overlap the next

    public ThumbnailStreamer(List<MonitorInfo> monitors, Action<string> send)
    {
        _mons = monitors; _send = send;
        _jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _jpegParams = new EncoderParameters(1);
        _jpegParams.Param[0] = new EncoderParameter(Encoder.Quality, 45L);
    }

    public void Start() => _timer ??= new System.Threading.Timer(_ => Tick(), null, 250, IntervalMs);

    private void Tick()
    {
        if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            foreach (var m in _mons)
            {
                try
                {
                    var msg = CaptureOne(m);
                    if (msg != null) _send(msg);
                }
                catch { /* skip a display this tick */ }
            }
        }
        finally { System.Threading.Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Grab + encode one monitor's thumbnail; null if it is unchanged since last tick.</summary>
    private string? CaptureOne(MonitorInfo m)
    {
        int tw = ThumbWidth;
        int th = Math.Max(1, (int)Math.Round((double)ThumbWidth * m.Height / m.Width));
        if (!_thumbs.TryGetValue(m.Index, out var thumb) || thumb.Width != tw || thumb.Height != th)
        {
            thumb?.Dispose();
            thumb = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
            _thumbs[m.Index] = thumb;
            _lastJpeg.Remove(m.Index);
        }

        // Scale during the blit: screen DC → thumbnail DC in one StretchBlt (HALFTONE for
        // decent quality). Avoids grabbing a full-resolution frame just to shrink it.
        using (var g = Graphics.FromImage(thumb))
        {
            IntPtr dst = g.GetHdc();
            IntPtr src = GetDC(IntPtr.Zero);
            try
            {
                SetStretchBltMode(dst, HALFTONE);
                SetBrushOrgEx(dst, 0, 0, IntPtr.Zero);
                StretchBlt(dst, 0, 0, tw, th, src, m.X, m.Y, m.Width, m.Height, SRCCOPY);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, src);
                g.ReleaseHdc(dst);
            }
        }

        using var ms = new MemoryStream();
        thumb.Save(ms, _jpeg, _jpegParams);
        var bytes = ms.ToArray();
        if (_lastJpeg.TryGetValue(m.Index, out var last) && bytes.AsSpan().SequenceEqual(last))
            return null;   // static monitor → no traffic
        _lastJpeg[m.Index] = bytes;
        return $"{{\"t\":\"thumb\",\"i\":{m.Index},\"w\":{tw},\"h\":{th},\"img\":\"{Convert.ToBase64String(bytes)}\"}}";
    }

    public void Dispose()
    {
        _timer?.Dispose(); _timer = null;
        foreach (var t in _thumbs.Values) t.Dispose();
        _thumbs.Clear();
        _lastJpeg.Clear();
    }

    // --------------------------- P/Invoke ---------------------------
    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                          IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);
}
