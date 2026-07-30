using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Injects mouse + keyboard via SendInput.
///
/// Mouse uses ABSOLUTE | VIRTUALDESK with 0..65535 coords over the whole virtual
/// desktop, so multi-monitor and per-monitor DPI don't corrupt the mapping. The
/// viewer sends coords normalized 0..1 against the *captured image*; we map those
/// onto the primary monitor's slice of the virtual desktop.
///
/// Keyboard: the master (also Windows) sends a Windows virtual-key code. We convert
/// it to a scancode with MapVirtualKey and inject with KEYEVENTF_SCANCODE so raw-input
/// apps and games see real keys. A legacy string path (KeyMap) remains for the
/// browser viewer, which sends DOM KeyboardEvent.code values.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InputInjector
{
    private static readonly int VLeft   = GetSystemMetrics(SM_XVIRTUALSCREEN);
    private static readonly int VTop    = GetSystemMetrics(SM_YVIRTUALSCREEN);
    private static readonly int VWidth  = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    private static readonly int VHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

    // The captured region in virtual-desktop pixels; the viewer's normalized coords
    // are relative to THIS region. Defaults to the primary monitor; updated on switch.
    private static int _rx, _ry, _rw = 1, _rh = 1;

    /// <summary>Set the region the viewer is currently seeing (its coords map into it).</summary>
    public static void SetRegion(int x, int y, int w, int h)
    {
        _rx = x; _ry = y; _rw = Math.Max(1, w); _rh = Math.Max(1, h);
    }

    private static (int ax, int ay) ToAbsolute(double nx, double ny)
    {
        // normalized within the captured region -> virtual-desktop pixel -> 0..65535
        double screenX = _rx + nx * _rw;
        double screenY = _ry + ny * _rh;
        double vx = (screenX - VLeft) / VWidth;
        double vy = (screenY - VTop) / VHeight;
        int ax = (int)Math.Clamp(vx * 65535.0, 0, 65535);
        int ay = (int)Math.Clamp(vy * 65535.0, 0, 65535);
        return (ax, ay);
    }

    public static void MouseMove(double nx, double ny)
    {
        var (ax, ay) = ToAbsolute(nx, ny);
        SendMouse(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0);
    }

    public static void MouseButton(double nx, double ny, int btn, bool down)
    {
        var (ax, ay) = ToAbsolute(nx, ny);
        uint flags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | btn switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN   : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_RIGHTDOWN  : MOUSEEVENTF_RIGHTUP,
            2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => 0u,
        };
        SendMouse(ax, ay, flags, 0);
    }

    public static void MouseWheel(int delta)
        => SendMouse(0, 0, MOUSEEVENTF_WHEEL, (uint)delta);

    /// <summary>Inject by Windows virtual-key (sent by the WPF master).</summary>
    public static void KeyVirtual(int vk, bool down)
    {
        uint sc = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC_EX);
        ushort scan = (ushort)(sc & 0xFF);
        byte prefix = (byte)((sc >> 8) & 0xFF);
        bool extended = prefix == 0xE0 || prefix == 0xE1;

        if (scan == 0)
        {
            // No scancode mapping (e.g. some media keys): fall back to virtual-key.
            SendKey(0, (ushort)vk, down ? 0u : KEYEVENTF_KEYUP);
            return;
        }
        uint flags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u);
        SendKey(scan, 0, flags);
    }

    /// <summary>Legacy path: inject by DOM KeyboardEvent.code (browser viewer).</summary>
    public static void KeyCode(string code, bool down)
    {
        if (!KeyMap.TryGetScanCode(code, out ushort scan, out bool extended)) return;
        uint flags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u);
        SendKey(scan, 0, flags);
    }

    private static void SendKey(ushort scan, ushort vk, uint flags)
    {
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouse(int ax, int ay, uint flags, uint data)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            U = { mi = new MOUSEINPUT { dx = ax, dy = ay, mouseData = data, dwFlags = flags } }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    // --------------------------- P/Invoke ---------------------------
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002,
        MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010,
        MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800,
        MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002,
        KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC_EX = 4;

    private const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
