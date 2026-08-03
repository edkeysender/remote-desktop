using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Watches for a new top-level window appearing (foreground change or window show) and
/// reports which monitor it landed on, so the viewer can flag a display the operator
/// isn't currently looking at. Uses out-of-context WinEvent hooks on a dedicated STA
/// thread with its own message loop (required for hook callbacks to fire).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowWatcher : IDisposable
{
    private readonly List<MonitorInfo> _monitors;
    private readonly Action<int> _onNewWindow;      // monitor Index that got a new window
    private readonly WinEventDelegate _proc;        // kept alive for the hook's lifetime
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookFg, _hookShow;
    private readonly Dictionary<int, long> _lastFire = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public WindowWatcher(List<MonitorInfo> monitors, Action<int> onNewWindow)
    {
        _monitors = monitors; _onNewWindow = onNewWindow; _proc = Callback;
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "WindowWatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hookFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _hookShow = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private void Callback(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
            // top-level, visible, non-tool window with real area
            if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;
            if (!IsWindowVisible(hwnd)) return;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return;
            if (!GetWindowRect(hwnd, out RECT r)) return;
            if ((r.right - r.left) < 160 || (r.bottom - r.top) < 120) return;

            int idx = MonitorIndexOf(hwnd);
            if (idx < 0) return;

            // throttle per-monitor so a burst of shows doesn't spam the channel
            long now = _clock.ElapsedMilliseconds;
            if (_lastFire.TryGetValue(idx, out var last) && now - last < 1500) return;
            _lastFire[idx] = now;
            _onNewWindow(idx);
        }
        catch { /* never let a hook callback throw */ }
    }

    private int MonitorIndexOf(IntPtr hwnd)
    {
        IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return -1;
        foreach (var m in _monitors)
            if (m.X == mi.rcMonitor.left && m.Y == mi.rcMonitor.top) return m.Index;
        return -1;
    }

    public void Dispose()
    {
        if (_hookFg != IntPtr.Zero) { UnhookWinEvent(_hookFg); _hookFg = IntPtr.Zero; }
        if (_hookShow != IntPtr.Zero) { UnhookWinEvent(_hookShow); _hookShow = IntPtr.Zero; }
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread = null; _threadId = 0;
    }

    // --------------------------- P/Invoke ---------------------------
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0, CHILDID_SELF = 0;
    private const uint GA_ROOT = 2;
    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint WM_QUIT = 0x0012;

    private delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
}
