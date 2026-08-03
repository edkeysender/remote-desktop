using System.Text.Json;
using SIPSorcery.Net;

namespace RemoteDesktop.Shared;

/// <summary>
/// Builds the WebRTC ICE server list from the org's network settings (delivered by the
/// relay in the register/connected messages). An empty STUN list means host candidates
/// only — pure LAN-direct with no external dependency. TURN, if set, is a last-resort relay.
/// </summary>
public static class IceConfig
{
    public static List<RTCIceServer> Default() =>
        new() { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } };

    /// <summary>Parse an <c>ice</c> object ({stun:[…], turnUrl, turnUser, turnPass}); null → default.</summary>
    public static List<RTCIceServer> FromJson(JsonElement ice)
    {
        if (ice.ValueKind != JsonValueKind.Object) return Default();
        var list = new List<RTCIceServer>();
        if (ice.TryGetProperty("stun", out var s) && s.ValueKind == JsonValueKind.Array)
            foreach (var u in s.EnumerateArray())
            {
                var url = u.GetString();
                if (!string.IsNullOrWhiteSpace(url)) list.Add(new RTCIceServer { urls = url });
            }
        if (ice.TryGetProperty("turnUrl", out var tu) && tu.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tu.GetString()))
            list.Add(new RTCIceServer
            {
                urls = tu.GetString(),
                username = ice.TryGetProperty("turnUser", out var un) ? un.GetString() : null,
                credential = ice.TryGetProperty("turnPass", out var tp) ? tp.GetString() : null,
            });
        // An explicitly-empty STUN list (LAN-only) is honored — return it as-is.
        return list;
    }
}
