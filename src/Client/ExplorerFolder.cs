using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesktop.Client;

/// <summary>
/// Finds the folder path of the File Explorer window the user currently has open on this
/// machine, so a dropped file can land where they're looking instead of always Downloads.
/// Best-effort: returns the foreground Explorer's path, else any open Explorer, else null.
/// Uses the Shell.Application COM automation object on a short-lived STA thread.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExplorerFolder
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public static string? Foreground()
    {
        IntPtr fg = GetForegroundWindow();
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
}
