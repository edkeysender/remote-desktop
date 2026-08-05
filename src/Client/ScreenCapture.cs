using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>One physical display (bounds in virtual-desktop coordinates).</summary>
public readonly record struct MonitorInfo(int Index, int X, int Y, int Width, int Height, bool Primary, string Name)
{
    public Rectangle Bounds => new(X, Y, Width, Height);
}

/// <summary>
/// Primary/region grabber. Captures an arbitrary rectangle of the virtual desktop
/// (a single monitor, or all of them) into a tightly-packed BGR24 buffer for the
/// VP8 encoder. The region can be changed at runtime to switch monitors.
/// Phase 3 replaces this with DXGI Desktop Duplication + hardware H.264.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapture : IDisposable
{
    private Rectangle _region;
    private Bitmap _bmp;
    private Graphics _gfx;
    private byte[] _bgr;

    // DXGI Desktop Duplication path (single-monitor regions only). Falls back to GDI on any
    // failure; after a fault we retry re-creating it after a short cooldown.
    private DxgiDuplicator? _dxgi;
    private long _dxgiRetryAt;                 // Environment.TickCount64 gate for re-creation
    private bool _dxgiEligible;               // region is exactly one monitor → DXGI can apply

    public int Width => _region.Width;
    public int Height => _region.Height;
    public Rectangle Region => _region;

    /// <summary>Which backend produced the last frame ("dxgi" or "gdi"), for diagnostics.</summary>
    public string Backend { get; private set; } = "gdi";

    public ScreenCapture(Rectangle region) => Init(region, out _bmp, out _gfx, out _bgr);

    /// <summary>Switch the captured region (e.g. a different monitor or all).</summary>
    public void SetRegion(Rectangle region)
    {
        if (region == _region) return;
        _gfx.Dispose(); _bmp.Dispose();
        _dxgi?.Dispose(); _dxgi = null;
        Init(region, out _bmp, out _gfx, out _bgr);
    }

    private void Init(Rectangle region, out Bitmap bmp, out Graphics gfx, out byte[] bgr)
    {
        _region = region;
        bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        gfx = Graphics.FromImage(bmp);
        bgr = new byte[region.Width * region.Height * 3];
        // DXGI duplicates a whole output, so it applies only when the region is exactly one
        // monitor (the combined all-monitors view stays on GDI).
        _dxgiEligible = EnumerateMonitors().Any(m => m.Bounds == region);
        _dxgiRetryAt = 0;
        TryInitDxgi();
    }

    private void TryInitDxgi()
    {
        if (!_dxgiEligible) return;
        try { _dxgi = DxgiDuplicator.TryCreate(_region); } catch { _dxgi = null; }
    }

    /// <summary>Grab one frame as tightly-packed BGR24 (stride == width*3). Buffer is reused.</summary>
    public byte[] CaptureBgr()
    {
        // Prefer GPU capture. On timeout (no change) return the last buffer; on error, drop
        // to GDI and schedule a retry so a transient loss (UAC, mode change) self-heals.
        if (_dxgi == null && _dxgiEligible && Environment.TickCount64 >= _dxgiRetryAt)
        {
            _dxgiRetryAt = Environment.TickCount64 + 3000;
            TryInitDxgi();
        }
        if (_dxgi != null)
        {
            try
            {
                _dxgi.TryCapture(_bgr);   // false = unchanged → keep previous _bgr
                Backend = "dxgi";
                return _bgr;
            }
            catch
            {
                _dxgi.Dispose(); _dxgi = null;
                _dxgiRetryAt = Environment.TickCount64 + 3000;   // cool down before retrying DXGI
            }
        }

        // ---- GDI fallback ----
        _gfx.CopyFromScreen(_region.Left, _region.Top, 0, 0, _region.Size, CopyPixelOperation.SourceCopy);
        var data = _bmp.LockBits(new Rectangle(0, 0, _region.Width, _region.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = _region.Width * 3;
            if (data.Stride == rowBytes) Marshal.Copy(data.Scan0, _bgr, 0, _bgr.Length);
            else for (int y = 0; y < _region.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, _bgr, y * rowBytes, rowBytes);
        }
        finally { _bmp.UnlockBits(data); }
        Backend = "gdi";
        return _bgr;
    }

    public void Dispose() { _dxgi?.Dispose(); _gfx.Dispose(); _bmp.Dispose(); }

    // ---------------- monitor discovery / region helpers ----------------

    /// <summary>The full virtual desktop rectangle (all monitors, may have negative origin).</summary>
    public static Rectangle VirtualDesktop() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        int i = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                bool primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                list.Add(new MonitorInfo(i, mi.rcMonitor.left, mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top,
                    primary, mi.szDevice ?? $"Display {i + 1}"));
            }
            i++;
            return true;
        }, IntPtr.Zero);
        // Primary first, then by X so the ordering is stable and intuitive.
        list.Sort((a, b) => a.Primary != b.Primary ? (a.Primary ? -1 : 1) : a.X.CompareTo(b.X));
        return list;
    }

    /// <summary>Primary monitor bounds (fallback if enumeration is empty).</summary>
    public static Rectangle PrimaryBounds()
    {
        foreach (var m in EnumerateMonitors()) if (m.Primary) return m.Bounds;
        return new Rectangle(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }

    // --------------------------- P/Invoke ---------------------------
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
