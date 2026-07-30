using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Phase-0 screen grabber: GDI BitBlt of the primary monitor into a JPEG.
/// Simple and portable; not the fast path. Phase 3 replaces this whole class
/// with DXGI Desktop Duplication (dirty-rects, GPU staging) feeding a
/// Media Foundation H.264 encoder.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapture : IDisposable
{
    private readonly Rectangle _bounds;
    private readonly Bitmap _bmp;
    private readonly Graphics _gfx;
    private readonly ImageCodecInfo _jpegCodec;
    private readonly EncoderParameters _encParams;

    public int Width => _bounds.Width;
    public int Height => _bounds.Height;

    public ScreenCapture(long jpegQuality = 50)
    {
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        _bounds = new Rectangle(0, 0, w, h);

        _bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        _gfx = Graphics.FromImage(_bmp);

        _jpegCodec = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _encParams = new EncoderParameters(1)
        {
            Param = { [0] = new EncoderParameter(Encoder.Quality, jpegQuality) }
        };
    }

    /// <summary>Grab one frame and return JPEG bytes.</summary>
    public byte[] CaptureJpeg()
    {
        _gfx.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, _bounds.Size,
            CopyPixelOperation.SourceCopy);

        using var ms = new MemoryStream(64 * 1024);
        _bmp.Save(ms, _jpegCodec, _encParams);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _encParams.Dispose();
        _gfx.Dispose();
        _bmp.Dispose();
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
