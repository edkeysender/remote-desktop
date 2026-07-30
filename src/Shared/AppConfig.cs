using System.Text.Json;

namespace RemoteDesktop.Shared;

/// <summary>
/// Tiny JSON settings store under %AppData%\RemoteDesktop\&lt;name&gt;.json.
/// Used by both apps to remember the signaling server URL (and, on the client,
/// a persistent password if the user sets one).
/// </summary>
public sealed class AppConfig
{
    public string ServerUrl { get; set; } = "ws://localhost:8080";

    /// <summary>Client only: if set, used instead of a random per-launch password.</summary>
    public string? FixedPassword { get; set; }

    private static string PathFor(string name) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FTD Remote", name + ".json");

    public static AppConfig Load(string name)
    {
        try
        {
            var path = PathFor(name);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch { }
        return new AppConfig();
    }

    public void Save(string name)
    {
        try
        {
            var path = PathFor(name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
