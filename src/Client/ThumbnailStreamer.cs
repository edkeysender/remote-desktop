using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Streams a low-fps (1 fps) downscaled JPEG thumbnail of every monitor to the viewer
/// over the "thumbs" data channel, so the viewer's monitor rail and all-monitors overview
/// can show live miniatures of each display — cheap because the grabs are tiny and slow.
/// One message per monitor: {"t":"thumb","i":idx,"w":w,"h":h,"img":"&lt;base64 jpeg&gt;"}.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThumbnailStreamer : IDisposable
{
    private const int ThumbWidth = 192;   // matches the rail's expanded thumbnail (retina-ish)
    private readonly List<MonitorInfo> _mons;
    private readonly Action<string> _send;
    private readonly ImageCodecInfo _jpeg;
    private readonly EncoderParameters _jpegParams;
    private readonly Dictionary<int, (Bitmap full, Bitmap thumb)> _cache = new();
    private System.Threading.Timer? _timer;
    private int _busy;   // 0/1 guard so a slow tick can't overlap the next

    public ThumbnailStreamer(List<MonitorInfo> monitors, Action<string> send)
    {
        _mons = monitors; _send = send;
        _jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _jpegParams = new EncoderParameters(1);
        _jpegParams.Param[0] = new EncoderParameter(Encoder.Quality, 45L);
    }

    public void Start() => _timer ??= new System.Threading.Timer(_ => Tick(), null, 250, 1000);

    private void Tick()
    {
        if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            foreach (var m in _mons)
            {
                try { _send(CaptureOne(m)); } catch { /* skip a display this tick */ }
            }
        }
        finally { System.Threading.Interlocked.Exchange(ref _busy, 0); }
    }

    private string CaptureOne(MonitorInfo m)
    {
        int tw = ThumbWidth;
        int th = Math.Max(1, (int)Math.Round((double)ThumbWidth * m.Height / m.Width));
        if (!_cache.TryGetValue(m.Index, out var pair) ||
            pair.full.Width != m.Width || pair.thumb.Width != tw || pair.thumb.Height != th)
        {
            pair.full?.Dispose(); pair.thumb?.Dispose();
            pair = (new Bitmap(m.Width, m.Height, PixelFormat.Format24bppRgb),
                    new Bitmap(tw, th, PixelFormat.Format24bppRgb));
            _cache[m.Index] = pair;
        }
        using (var g = Graphics.FromImage(pair.full))
            g.CopyFromScreen(m.X, m.Y, 0, 0, new Size(m.Width, m.Height), CopyPixelOperation.SourceCopy);
        using (var g2 = Graphics.FromImage(pair.thumb))
        {
            g2.InterpolationMode = InterpolationMode.Bilinear;
            g2.DrawImage(pair.full, 0, 0, tw, th);
        }
        using var ms = new MemoryStream();
        pair.thumb.Save(ms, _jpeg, _jpegParams);
        string b64 = Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
        return $"{{\"t\":\"thumb\",\"i\":{m.Index},\"w\":{tw},\"h\":{th},\"img\":\"{b64}\"}}";
    }

    public void Dispose()
    {
        _timer?.Dispose(); _timer = null;
        foreach (var p in _cache.Values) { p.full.Dispose(); p.thumb.Dispose(); }
        _cache.Clear();
    }
}
