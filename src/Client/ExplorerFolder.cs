using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RemoteDesktop.Client;

/// <summary>
/// Resolves where a pasted/dropped file should land on this machine: the folder of the
/// File Explorer window the user is looking at, the Desktop when the desktop itself is
/// foreground, else any open Explorer window, else null (caller falls back to Downloads).
/// Explorer windows are read via the Shell.Application COM automation object on a
/// short-lived STA thread. The Desktop path is resolved against the interactive user's
/// token when running as SYSTEM (unattended worker), so redirection (OneDrive) is honoured
/// and it never lands in the SYSTEM profile.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExplorerFolder
{
    public static string? Foreground()
    {
        IntPtr fg = GetForegroundWindow();

        // The desktop itself is a valid destination: pasting while "on the desktop"
        // should land there, not in Downloads. Desktop window classes: Progman
        // (normal) and WorkerW (when the wallpaper host owns the desktop).
        var cls = new StringBuilder(64);
        if (GetClassName(fg, cls, cls.Capacity) > 0 && cls.ToString() is "Progman" or "WorkerW")
        {
            var desk = DesktopDir();
            if (desk != null) return desk;
        }

        string? result = null;
        var t = new Thread(() => { result = Query(fg); }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(2000);
        return result;
    }

    private static string? Query(IntPtr fg)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();
            int count = windows.Count;
            string? anyExplorer = null;
            for (int i = 0; i < count; i++)
            {
                dynamic? w = windows.Item(i);
                if (w == null) continue;
                // Only real Explorer windows (not Internet Explorer) host a filesystem folder.
                string? full = null; try { full = (string)w.FullName; } catch { }
                if (full == null || !full.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;

                string? path = null;
                try { path = (string)w.Document.Folder.Self.Path; } catch { }
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;

                long hwnd = 0; try { hwnd = (long)w.HWND; } catch { }
                if ((IntPtr)hwnd == fg) return path;   // the one they're actually looking at
                anyExplorer ??= path;
            }
            return anyExplorer;
        }
        catch { return null; }
    }

    /// <summary>The interactive user's Desktop folder (redirection-aware), or null.</summary>
    private static string? DesktopDir()
    {
        try
        {
            // Normal case: the host runs as the logged-in user.
            if (!Environment.UserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
            {
                var p = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                return string.IsNullOrEmpty(p) ? null : p;
            }
            // Unattended worker (SYSTEM in the user's session): resolve the Desktop against
            // the session's explorer.exe token — SYSTEM's own profile is never the answer.
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    if (p.SessionId != session) continue;
                    if (!OpenProcessToken(p.Handle, TOKEN_QUERY | TOKEN_IMPERSONATE | TOKEN_DUPLICATE, out IntPtr tok)) continue;
                    try
                    {
                        if (SHGetKnownFolderPath(ref FOLDERID_Desktop, 0, tok, out IntPtr pPath) == 0)
                        {
                            var s = Marshal.PtrToStringUni(pPath);
                            Marshal.FreeCoTaskMem(pPath);
                            if (!string.IsNullOrEmpty(s) && Directory.Exists(s)) return s;
                        }
                    }
                    finally { CloseHandle(tok); }
                }
            }
            return null;
        }
        catch { return null; }
    }

    // --------------------------- P/Invoke ---------------------------
    private const uint TOKEN_QUERY = 0x0008, TOKEN_IMPERSONATE = 0x0004, TOKEN_DUPLICATE = 0x0002;
    private static Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
