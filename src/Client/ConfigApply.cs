using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RemoteDesktop.Client;

/// <summary>
/// Applies a device configuration and reports a per-check result. Actions the current
/// user can perform (wallpaper, read-only checks) run directly; ones needing elevation
/// (login-screen background, installing VC++/OpenSSH) are reported as "needs-admin" for
/// now — the unattended SYSTEM service will perform those in a later pass. The check list
/// is intentionally easy to extend.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConfigApply
{
    public sealed record Spec(string Name, string? Wallpaper, string? LoginBackground,
        bool CheckWindowsActivated, bool InstallVcredist, bool InstallOpenSSH, string ComputerNameStandard);

    public static Spec Parse(JsonElement a)
    {
        string? S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool B(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        return new Spec(S("name") ?? "Configuration", S("wallpaper"), S("loginBackground"),
            B("checkWindowsActivated"), B("installVcredist"), B("installOpenSSH"), S("computerNameStandard") ?? "");
    }

    public static Dictionary<string, object?> Run(Spec c)
    {
        var results = new List<object>();
        void R(string key, string label, string status, string detail) =>
            results.Add(new { key, label, status, detail });

        if (c.Wallpaper != null) TryWallpaper(c.Wallpaper, R);
        if (c.LoginBackground != null) TryLoginBackground(c.LoginBackground, R);
        if (!string.IsNullOrWhiteSpace(c.ComputerNameStandard)) CheckComputerName(c.ComputerNameStandard, R);
        if (c.CheckWindowsActivated) CheckWindowsActivated(R);
        if (c.InstallVcredist) CheckVcredist(R);
        if (c.InstallOpenSSH) CheckOpenSsh(R);

        return new() { ["ok"] = true, ["results"] = results };
    }

    private static void TryWallpaper(string dataUrl, Action<string, string, string, string> R)
    {
        try
        {
            var path = SaveImage(dataUrl, "wallpaper");
            if (path == null) { R("wallpaper", "Desktop wallpaper", "fail", "unsupported image"); return; }
            // SPI_SETDESKWALLPAPER = 0x0014; update + persist + broadcast
            bool ok = SystemParametersInfo(0x0014, 0, path, 0x01 | 0x02);
            R("wallpaper", "Desktop wallpaper", ok ? "ok" : "fail", ok ? "applied" : "SystemParametersInfo failed");
        }
        catch (Exception ex) { R("wallpaper", "Desktop wallpaper", "fail", ex.Message); }
    }

    private static void TryLoginBackground(string dataUrl, Action<string, string, string, string> R)
    {
        try
        {
            var path = SaveImage(dataUrl, "loginbg");
            if (path == null) { R("loginBackground", "Login/lock background", "fail", "unsupported image"); return; }
            // HKLM policy — requires admin; will throw for a normal user.
            using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Personalization");
            k!.SetValue("LockScreenImage", path, RegistryValueKind.String);
            R("loginBackground", "Login/lock background", "ok", "set via policy");
        }
        catch (UnauthorizedAccessException) { R("loginBackground", "Login/lock background", "needs-admin", "requires the unattended service (admin)"); }
        catch (Exception ex) { R("loginBackground", "Login/lock background", "needs-admin", ex.Message); }
    }

    private static void CheckComputerName(string standard, Action<string, string, string, string> R)
    {
        var name = Environment.MachineName;
        bool ok;
        try { ok = Regex.IsMatch(name, standard, RegexOptions.IgnoreCase); }
        catch { ok = name.StartsWith(standard, StringComparison.OrdinalIgnoreCase); }   // not a regex → treat as prefix
        R("computerName", "Computer name standard", ok ? "ok" : "warn",
            ok ? $"{name} matches" : $"{name} does not match \"{standard}\"");
    }

    private static void CheckWindowsActivated(Action<string, string, string, string> R)
    {
        try
        {
            var outp = RunPs("(Get-CimInstance SoftwareLicensingProduct -Filter \"PartialProductKey IS NOT NULL\" | Where-Object LicenseStatus -eq 1 | Measure-Object).Count");
            bool activated = int.TryParse(outp.Trim(), out var n) && n > 0;
            R("windowsActivated", "Windows activation", activated ? "ok" : "warn", activated ? "activated" : "NOT activated");
        }
        catch (Exception ex) { R("windowsActivated", "Windows activation", "fail", ex.Message); }
    }

    private static void CheckVcredist(Action<string, string, string, string> R)
    {
        bool present = false;
        foreach (var sub in new[] { @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64", @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" })
        {
            try { using var k = Registry.LocalMachine.OpenSubKey(sub); if (k?.GetValue("Installed") is int i && i == 1) present = true; } catch { }
        }
        R("vcredist", "VC++ redistributable", present ? "ok" : "needs-admin",
            present ? "installed" : "missing — install requires the unattended service (admin)");
    }

    private static void CheckOpenSsh(Action<string, string, string, string> R)
    {
        var ssh = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        bool present = File.Exists(ssh);
        R("openssh", "OpenSSH", present ? "ok" : "needs-admin",
            present ? "installed" : "not installed — Add-WindowsCapability requires the unattended service (admin)");
    }

    private static string? SaveImage(string dataUrl, string stem)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return null;
        var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        var ext = dataUrl.Contains("image/png") ? ".png" : dataUrl.Contains("image/jpeg") ? ".jpg" : ".img";
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FTD Remote", "config");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, stem + ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string RunPs(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15000);
        return outp;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint uParam, string vParam, uint winIni);
}
