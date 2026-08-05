using System.Drawing;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesktop.Client;

/// <summary>
/// GPU screen capture via DXGI Desktop Duplication. Captures one whole output (monitor)
/// into a tightly-packed BGR24 buffer — far lower CPU + latency than GDI BitBlt, and it
/// only produces a frame when the desktop actually changed. Falls back are handled by the
/// caller (<see cref="ScreenCapture"/>): any failure here throws / returns null and the
/// caller reverts to GDI.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DxgiDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly IDXGIOutputDuplication _dupl;
    private ID3D11Texture2D? _staging;

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
    /// Try to grab the next frame into <paramref name="dst"/> (BGR24, tightly packed,
    /// Width*Height*3). Returns true if a fresh frame was written; false on timeout (no
    /// change — leave the previous contents). Throws on access-lost / device errors so the
    /// caller can fall back to GDI.
    /// </summary>
    public unsafe bool TryCapture(byte[] dst, int timeoutMs = 100)
    {
        var res = _dupl.AcquireNextFrame((uint)timeoutMs, out _, out IDXGIResource? desktop);
        if (res == Vortice.DXGI.ResultCode.WaitTimeout) return false;
        if (!res.Success) throw new InvalidOperationException("AcquireNextFrame failed: " + res.Code);

        try
        {
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
                int pitch = (int)map.RowPitch;
                byte* src = (byte*)map.DataPointer;
                fixed (byte* dstP = dst)
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* s = src + (long)y * pitch;    // BGRA source
                        byte* d = dstP + (long)y * Width * 3; // BGR dest
                        for (int x = 0; x < w; x++)
                        {
                            d[0] = s[0]; d[1] = s[1]; d[2] = s[2];   // B, G, R (drop A)
                            s += 4; d += 3;
                        }
                    }
                }
            }
            finally { _ctx.Unmap(_staging, 0); }
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
