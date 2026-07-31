using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Lightweight host health sampler for monitoring: CPU %, memory %, and system-drive
/// usage %. Uses Win32 (GetSystemTimes / GlobalMemoryStatusEx) + DriveInfo — no extra
/// dependencies. CPU is a delta between successive Sample() calls. Temperature is not
/// collected here (needs LibreHardwareMonitor); left for a later pass.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemMetrics
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _have;

    public (int cpu, int mem, int disk) Sample()
    {
        int cpu = SampleCpu();
        int mem = SampleMem();
        int disk = SampleDisk();
        return (cpu, mem, disk);
    }

    private int SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong i = ToU(idle), k = ToU(kernel), u = ToU(user);
        if (!_have) { _prevIdle = i; _prevKernel = k; _prevUser = u; _have = true; return 0; }
        ulong dIdle = i - _prevIdle, dKernel = k - _prevKernel, dUser = u - _prevUser;
        _prevIdle = i; _prevKernel = k; _prevUser = u;
        ulong total = dKernel + dUser;                 // kernel already includes idle
        if (total == 0) return 0;
        double busy = (double)(total - dIdle) / total; // subtract idle for busy fraction
        return (int)Math.Clamp(Math.Round(busy * 100), 0, 100);
    }

    private static int SampleMem()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (int)Math.Clamp(m.dwMemoryLoad, 0, 100) : 0;
    }

    private static int SampleDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new DriveInfo(root);
            if (!d.IsReady || d.TotalSize <= 0) return 0;
            double used = (double)(d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
            return (int)Math.Clamp(Math.Round(used * 100), 0, 100);
        }
        catch { return 0; }
    }

    private static ulong ToU(System.Runtime.InteropServices.ComTypes.FILETIME f)
        => ((ulong)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
        out System.Runtime.InteropServices.ComTypes.FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
