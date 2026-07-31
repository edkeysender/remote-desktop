using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Shared;

/// <summary>A group defined on the server.</summary>
public sealed record DirectoryGroup(string Id, string Name);

/// <summary>A client the server knows about, as shown in the Master's picker.</summary>
public sealed record DirectoryClientInfo(string Id, string Name, string? GroupId, bool Online, bool Busy);

/// <summary>Groups + clients fetched from the server for the Master's connection picker.</summary>
public sealed record DirectoryData(IReadOnlyList<DirectoryGroup> Groups, IReadOnlyList<DirectoryClientInfo> Clients);

/// <summary>
/// Read-only client for the server's <c>/directory</c> endpoint: the same groups and
/// clients the admin panel manages, so the Master can browse groups and pick a machine
/// to connect to. Auth is HTTP Basic with the admin password.
/// </summary>
public sealed class DirectoryClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<DirectoryData> FetchAsync(string serverUrl, string password, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, HttpBase(serverUrl) + "/directory");
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + password));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Wrong directory password.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var groups = new List<DirectoryGroup>();
        foreach (var g in root.GetProperty("groups").EnumerateArray())
            groups.Add(new DirectoryGroup(g.GetProperty("id").GetString() ?? "", g.GetProperty("name").GetString() ?? ""));

        var clients = new List<DirectoryClientInfo>();
        foreach (var c in root.GetProperty("clients").EnumerateArray())
            clients.Add(new DirectoryClientInfo(
                c.GetProperty("id").GetString() ?? "",
                c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                c.TryGetProperty("groupId", out var gid) && gid.ValueKind == JsonValueKind.String ? gid.GetString() : null,
                c.TryGetProperty("online", out var on) && on.GetBoolean(),
                c.TryGetProperty("busy", out var bu) && bu.GetBoolean()));

        return new DirectoryData(groups, clients);
    }

    // ws://host:port/... → http://host:port   (wss → https)
    private static string HttpBase(string serverUrl)
    {
        var u = new Uri(serverUrl);
        var scheme = u.Scheme switch { "wss" => "https", "ws" => "http", var s => s };
        return $"{scheme}://{u.Host}:{u.Port}";
    }
}
