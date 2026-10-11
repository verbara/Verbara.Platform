using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.WebChat;

public interface IWebChatTransport
{
    /// <summary>
    /// Sends <paramref name="message"/> to every socket attached to <paramref name="sessionId"/> on this
    /// replica. Returns <c>true</c> when at least one socket took the frame and <c>false</c> when none did
    /// (no socket attached yet, every socket closed, or a send failed) — the caller records that as a
    /// failed delivery, never as sent.
    /// </summary>
    Task<bool> SendToClientAsync(string sessionId, MessageEnvelope message, CancellationToken ct);

    Task DisconnectAsync(string sessionId, CancellationToken ct);
}
