using System.Diagnostics;
using System.IO;

namespace RemoteDesktop.Master;

/// <summary>
/// The app is often just downloaded and run straight from the Downloads folder. On launch
/// from such a "loose" location we copy the exe into a proper per-user install directory
/// (%LocalAppData%\Programs\Hangar), add a Start Menu shortcut, and relaunch from there —
/// so it lives somewhere sane, updates cleanly, and isn't cluttering Downloads.
/// Settings live in %AppData% (independent of exe path), so nothing is lost in the move.
/// </summary>
public static class SelfInstall
{
    public static string InstallExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hangar", "RemoteControl.exe");

    /// <summary>If we're running loose, install to the per-user dir and relaunch. Returns true
    /// if the caller should exit (a relocated instance has been started).</summary>
    public static bool RelocateIfLoose(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var dir = Path.GetDirectoryName(exe)!;
            var target = InstallExe;
            if (PathEq(exe, target)) return false;      // already the installed copy
            if (!IsLoose(dir)) return false;            // installed elsewhere (e.g. via installer) — leave it

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Copy only when missing or changed, to avoid churn on every launch.
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(exe).Length)
                try { File.Copy(exe, target, overwrite: true); } catch { /* target in use; launch what's there */ }

            TryCreateShortcut(target);

            var psi = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target) };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    private static bool IsLoose(string dir)
    {
        var prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] loose = { Path.Combine(prof, "Downloads"), Path.Combine(prof, "Desktop"), Path.GetTempPath() };
        foreach (var l in loose) if (PathEq(dir, l)) return true;
        return dir.IndexOf(@"\Downloads", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool PathEq(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void TryCreateShortcut(string target)
    {
        try
        {
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Hangar.lnk");
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic sh = Activator.CreateInstance(t)!;
            dynamic sc = sh.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Description = "Hangar Remote";
            sc.Save();
        }
        catch { /* shortcut is best-effort */ }
    }
}
