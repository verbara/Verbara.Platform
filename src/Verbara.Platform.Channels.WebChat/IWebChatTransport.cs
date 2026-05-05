using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.WebChat;

public interface IWebChatTransport
{
    Task SendToClientAsync(string sessionId, MessageEnvelope message, CancellationToken ct);
    Task DisconnectAsync(string sessionId, CancellationToken ct);
}
