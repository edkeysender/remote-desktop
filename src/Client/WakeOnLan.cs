using System.Net;
using System.Net.Sockets;

namespace RemoteDesktop.Client;

/// <summary>
/// Sends a Wake-on-LAN magic packet for a target MAC. Run on an online host that shares
/// the LAN/subnet with the (offline) target — the relay picks such a peer.
/// </summary>
public static class WakeOnLan
{
    public static Dictionary<string, object?> Send(string? mac)
    {
        var bytes = ParseMac(mac);
        if (bytes == null) return HostCommands.Err("invalid MAC");

        // magic packet: 6x 0xFF then the 6-byte MAC repeated 16 times
        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 6; i < 102; i += 6) Array.Copy(bytes, 0, packet, i, 6);

        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            // Broadcast to the standard WOL ports on the local subnet.
            foreach (var port in new[] { 9, 7 })
                udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, port));
            return HostCommands.Ok();
        }
        catch (Exception ex) { return HostCommands.Err(ex.Message); }
    }

    private static byte[]? ParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = mac.Replace(":", "").Replace("-", "").Replace(".", "").Trim();
        if (hex.Length != 12) return null;
        var b = new byte[6];
        for (int i = 0; i < 6; i++)
            if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out b[i]))
                return null;
        return b;
    }
}
