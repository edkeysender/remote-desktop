using System.Drawing;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesktop.Client;

/// <summary>
/// GPU screen capture via DXGI Desktop Duplication. Captures one whole output (monitor)
/// into a BGRA32 buffer — far lower CPU + latency than GDI BitBlt — and reports, from the
/// duplication frame metadata, whether the desktop image actually changed, so callers can
/// skip encoding entirely on static screens (cursor-only updates don't count as change).
/// The destination can be a larger composite buffer (Show-All): the frame is written at
/// (dstX, dstY) with the caller's row stride. Fallbacks are handled by the caller
/// (<see cref="ScreenCapture"/>): any failure here throws / returns null and the caller
/// reverts to GDI.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DxgiDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly IDXGIOutputDuplication _dupl;
    private ID3D11Texture2D? _staging;
    private bool _everCopied;   // first acquired frame must be treated as changed

    public int Width { get; }
    public int Height { get; }

    private DxgiDuplicator(ID3D11Device device, ID3D11DeviceContext ctx, IDXGIOutputDuplication dupl, int w, int h)
    {
        _device = device; _ctx = ctx; _dupl = dupl; Width = w; Height = h;
    }

    /// <summary>Create a duplicator for the output whose desktop rectangle matches
    /// <paramref name="monitor"/> exactly. Returns null if none matches or setup fails.</summary>
    public static DxgiDuplicator? TryCreate(Rectangle monitor)
    {
        IDXGIFactory1? factory = null;
        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                    {
                        using (output)
                        {
                            var dc = output.Description.DesktopCoordinates;
                            int w = dc.Right - dc.Left, h = dc.Bottom - dc.Top;
                            if (dc.Left != monitor.X || dc.Top != monitor.Y || w != monitor.Width || h != monitor.Height)
                                continue;

                            // Create a D3D11 device on this adapter (Unknown driver = use the given adapter).
                            var hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                                DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device);
                            if (!hr.Success || device == null) return null;

                            var ctx = device.ImmediateContext;
                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            var dupl = output1.DuplicateOutput(device);
                            return new DxgiDuplicator(device, ctx, dupl, w, h);
                        }
                    }
                }
            }
            return null;
        }
        catch { return null; }
        finally { factory?.Dispose(); }
    }

    /// <summary>
    /// Try to grab the next frame into <paramref name="dst"/> (BGRA32) at pixel offset
    /// (<paramref name="dstX"/>, <paramref name="dstY"/>) with row stride
    /// <paramref name="dstStride"/> bytes. Sets <paramref name="changed"/> when the desktop
    /// image was actually updated (per the duplication metadata — cursor-only movement is
    /// not a change, and the GPU→CPU readback is skipped entirely for it). Returns false on
    /// timeout (nothing new — previous contents remain valid). Throws on access-lost /
    /// device errors so the caller can fall back to GDI.
    /// </summary>
    public unsafe bool TryCapture(byte[] dst, int dstStride, int dstX, int dstY, out bool changed, int timeoutMs = 10)
    {
        changed = false;
        var res = _dupl.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo info, out IDXGIResource? desktop);
        if (res == Vortice.DXGI.ResultCode.WaitTimeout) return false;
        if (!res.Success) throw new InvalidOperationException("AcquireNextFrame failed: " + res.Code);

        try
        {
            // LastPresentTime == 0 → only the mouse pointer moved; the desktop image is
            // unchanged and there is nothing to read back or encode. (The very first frame
            // after DuplicateOutput carries the current desktop and must always be copied.)
            if (_everCopied && info.LastPresentTime == 0 && info.AccumulatedFrames == 0)
                return true;

            using var tex = desktop!.QueryInterface<ID3D11Texture2D>();
            var desc = tex.Description;

            if (_staging == null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
            {
                _staging?.Dispose();
                var sdesc = desc;
                sdesc.Usage = ResourceUsage.Staging;
                sdesc.CPUAccessFlags = CpuAccessFlags.Read;
                sdesc.BindFlags = BindFlags.None;
                sdesc.MiscFlags = ResourceOptionFlags.None;
                _staging = _device.CreateTexture2D(sdesc);
            }

            _ctx.CopyResource(_staging, tex);
            var map = _ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int w = Math.Min(Width, (int)desc.Width), h = Math.Min(Height, (int)desc.Height);
                int srcPitch = (int)map.RowPitch, rowBytes = w * 4;
                byte* src = (byte*)map.DataPointer;
                fixed (byte* dstP = dst)
                {
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(
                            src + (long)y * srcPitch,
                            dstP + (long)(dstY + y) * dstStride + (long)dstX * 4,
                            rowBytes, rowBytes);
                }
            }
            finally { _ctx.Unmap(_staging, 0); }
            _everCopied = true;
            changed = true;
            return true;
        }
        finally
        {
            desktop?.Dispose();
            try { _dupl.ReleaseFrame(); } catch { }
        }
    }

    public void Dispose()
    {
        _staging?.Dispose();
        try { _dupl.Dispose(); } catch { }
        try { _ctx.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
