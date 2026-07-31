using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Shared;

/// <summary>A signed-in account session (token + who/where).</summary>
public sealed record AccountSession(string Token, string Email, string OrgName, bool IsAdmin);

/// <summary>A computer the signed-in user may connect to.</summary>
public sealed record AccountComputer(string DeviceToken, string Name, string? GroupId, string? RelayId, bool Online, bool Busy);

/// <summary>The groups + computers a user is allowed to use (server /api/my-computers).</summary>
public sealed record MyComputers(IReadOnlyList<DirectoryGroup> Groups, IReadOnlyList<AccountComputer> Computers, bool Admin);

/// <summary>
/// Talks to the account web API on the signaling server (same host/port, HTTP side):
/// sign in, validate a saved token, and fetch the user's allowed groups/computers.
/// The returned token is sent as a Bearer on WS register/connect for account auth.
/// </summary>
public sealed class AccountClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<AccountSession> LoginAsync(string serverUrl, string email, string password, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(HttpBase(serverUrl) + "/api/login",
            JsonContent(new { email, password }), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Wrong email or password.");
        resp.EnsureSuccessStatusCode();
        return ParseSession(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Validate a saved token; returns the session or null if it's no longer valid.</summary>
    public async Task<AccountSession?> MeAsync(string serverUrl, string token, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, HttpBase(serverUrl) + "/api/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return ParseSession(await resp.Content.ReadAsStringAsync(ct), token);
        }
        catch { return null; }
    }

    public async Task<MyComputers> MyComputersAsync(string serverUrl, string token, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, HttpBase(serverUrl) + "/api/my-computers");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var groups = new List<DirectoryGroup>();
        foreach (var g in root.GetProperty("groups").EnumerateArray())
            groups.Add(new DirectoryGroup(g.GetProperty("id").GetString() ?? "", g.GetProperty("name").GetString() ?? ""));

        var comps = new List<AccountComputer>();
        foreach (var c in root.GetProperty("computers").EnumerateArray())
            comps.Add(new AccountComputer(
                c.GetProperty("deviceToken").GetString() ?? "",
                c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                c.TryGetProperty("groupId", out var gid) && gid.ValueKind == JsonValueKind.String ? gid.GetString() : null,
                c.TryGetProperty("relayId", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() : null,
                c.TryGetProperty("online", out var on) && on.GetBoolean(),
                c.TryGetProperty("busy", out var bu) && bu.GetBoolean()));

        bool admin = root.TryGetProperty("admin", out var a) && a.GetBoolean();
        return new MyComputers(groups, comps, admin);
    }

    private static AccountSession ParseSession(string json, string? tokenOverride = null)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var token = tokenOverride ?? (r.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "");
        var user = r.GetProperty("user");
        var org = r.GetProperty("org");
        return new AccountSession(
            token,
            user.GetProperty("email").GetString() ?? "",
            org.GetProperty("name").GetString() ?? "",
            (user.TryGetProperty("role", out var role) ? role.GetString() : "") == "admin");
    }

    private static StringContent JsonContent(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static string HttpBase(string serverUrl)
    {
        var u = new Uri(serverUrl);
        var scheme = u.Scheme switch { "wss" => "https", "ws" => "http", var s => s };
        return $"{scheme}://{u.Host}:{u.Port}";
    }
}
