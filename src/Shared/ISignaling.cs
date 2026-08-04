using System.Text.Json;

namespace RemoteDesktop.Shared;

/// <summary>
/// The signaling channel used to negotiate a WebRTC session: exchange connect / offer /
/// answer / ICE and small control messages. Two implementations:
///   • <see cref="SignalingConnection"/> — WebSocket to the relay (cloud / Pi).
///   • <see cref="DirectSignaling"/>     — a direct TCP link between two apps on a LAN,
///     so local connections work with no server at all (TightVNC-style, connect by IP).
/// Events fire on a background thread; UI consumers marshal to their dispatcher.
/// </summary>
public interface ISignaling : IDisposable
{
    /// <summary>A JSON control message arrived. The element is valid only during the callback.</summary>
    event Action<JsonElement>? JsonReceived;
    /// <summary>The link closed (reason may be null).</summary>
    event Action<string?>? Closed;

    Task SendJsonAsync(object obj);
    Task CloseAsync();
}
