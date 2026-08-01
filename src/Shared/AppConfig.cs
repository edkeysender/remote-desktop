using System.Text.Json;

namespace RemoteDesktop.Shared;

/// <summary>
/// Tiny JSON settings store. Two scopes:
///   • per-user (<c>Load</c>/<c>Save</c>) under %AppData%\FTD Remote — attended apps.
///   • machine-wide (<c>LoadMachine</c>/<c>SaveMachine</c>) under %ProgramData%\FTD Remote —
///     used by the unattended service/worker (which runs as SYSTEM, not a user).
/// </summary>
public sealed class AppConfig
{
    public string ServerUrl { get; set; } = "ws://localhost:8080";

    /// <summary>Client only: if set, used instead of a random per-launch password.</summary>
    public string? FixedPassword { get; set; }

    /// <summary>Master only: password for the server's group directory (same as the admin password).</summary>
    public string? DirectoryPassword { get; set; }

    // ---- account session (unified app) ----
    /// <summary>Saved account session token (Bearer) for the signed-in user.</summary>
    public string? AuthToken { get; set; }
    /// <summary>Signed-in account email (for display + prefill).</summary>
    public string? AccountEmail { get; set; }
    /// <summary>Cached org name for display.</summary>
    public string? OrgName { get; set; }
    /// <summary>Pre-baked enrollment token that claims this PC into an org without an interactive login.</summary>
    public string? EnrollToken { get; set; }
    /// <summary>Host: show a consent prompt before accepting each incoming session (attended mode).</summary>
    public bool AskConsent { get; set; }

    // ---- unattended (machine) fields ----
    /// <summary>Stable host token → the server maps it to a fixed ID across restarts.</summary>
    public string? HostToken { get; set; }
    /// <summary>Fixed password for unattended connections.</summary>
    public string? UnattendedPassword { get; set; }
    /// <summary>Last ID the server assigned this host (written by the worker for display).</summary>
    public string? CurrentId { get; set; }

    private const string Dir = "FTD Remote";

    private static string UserPath(string name) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Dir, name + ".json");
    private static string MachinePath(string name) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Dir, name + ".json");

    public static AppConfig Load(string name) => LoadPath(UserPath(name));
    public void Save(string name) => SavePath(UserPath(name));

    public static AppConfig LoadMachine(string name) => LoadPath(MachinePath(name));
    public void SaveMachine(string name) => SavePath(MachinePath(name));

    private static AppConfig LoadPath(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch { }
        return new AppConfig();
    }

    private void SavePath(string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
