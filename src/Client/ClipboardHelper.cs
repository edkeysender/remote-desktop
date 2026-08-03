using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Sets the Windows clipboard text via raw Win32 (works from any thread, unlike the
/// WinForms/WPF Clipboard which needs STA). Used to push the operator's clipboard to
/// the remote machine during a session.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static bool SetText(string text)
    {
        text ??= "";
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(20); continue; }
            try
            {
                EmptyClipboard();
                int bytes = (text.Length + 1) * 2;             // UTF-16 + null terminator
                IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero) return false;
                IntPtr ptr = GlobalLock(hMem);
                try { Marshal.Copy((text + '\0').ToCharArray(), 0, ptr, text.Length + 1); }
                finally { GlobalUnlock(hMem); }
                // On success the system owns hMem; don't free it.
                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) { GlobalFree(hMem); return false; }
                return true;
            }
            finally { CloseClipboard(); }
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
}
