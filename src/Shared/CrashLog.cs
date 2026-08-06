using System.IO;

namespace RemoteDesktop.Shared;

/// <summary>
/// Last-resort fatal-crash breadcrumb. Native faults (e.g. an access violation inside a
/// GPU driver or FFmpeg) kill the process without any catch block running; this hooks
/// AppDomain.UnhandledException — which still fires for most of them — and appends the
/// exception to %LOCALAPPDATA%\Remotler\crash.log so a dead host leaves evidence behind.
/// </summary>
public static class CrashLog
{
    public static void Install(string component)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remotler");
                Directory.CreateDirectory(dir);
                var ver = typeof(CrashLog).Assembly.GetName().Version?.ToString(3) ?? "?";
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {component} v{ver} fatal:{Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* the crash log must never cause a crash */ }
        };
    }

    /// <summary>One-line diagnostic breadcrumb → %LOCALAPPDATA%\Remotler\diag.log (best-effort).</summary>
    public static void Note(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remotler");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "diag.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
