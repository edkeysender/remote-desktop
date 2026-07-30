using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Primary-monitor grabber. GDI BitBlt into a 24bpp bitmap; exposes both a
/// tightly-packed BGR buffer (for the VP8 encoder, Phase 2) and a JPEG (legacy
/// Phase-0 relay path). Phase 3 replaces this with DXGI Desktop Duplication +
/// hardware H.264.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapture : IDisposable
{
    private readonly Rectangle _bounds;
    private readonly Bitmap _bmp;               // Format24bppRgb => bytes are B,G,R
    private readonly Graphics _gfx;
    private readonly byte[] _bgr;               // reused tight BGR buffer

    public int Width => _bounds.Width;
    public int Height => _bounds.Height;

    public ScreenCapture()
    {
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        _bounds = new Rectangle(0, 0, w, h);
        _bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        _gfx = Graphics.FromImage(_bmp);
        _bgr = new byte[w * h * 3];
    }

    /// <summary>
    /// Grab one frame as tightly-packed BGR24 (stride == width*3). The returned
    /// array is reused between calls — encode/copy before the next capture.
    /// </summary>
    public byte[] CaptureBgr()
    {
        _gfx.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, _bounds.Size, CopyPixelOperation.SourceCopy);

        var data = _bmp.LockBits(_bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = _bounds.Width * 3;
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, _bgr, 0, _bgr.Length);
            }
            else
            {
                // Drop the per-row padding GDI adds when width*3 isn't 4-byte aligned.
                for (int y = 0; y < _bounds.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, _bgr, y * rowBytes, rowBytes);
            }
        }
        finally { _bmp.UnlockBits(data); }
        return _bgr;
    }

    public void Dispose()
    {
        _gfx.Dispose();
        _bmp.Dispose();
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
