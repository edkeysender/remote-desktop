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
