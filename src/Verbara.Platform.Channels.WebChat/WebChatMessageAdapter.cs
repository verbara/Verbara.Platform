using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat;

public sealed class WebChatMessageAdapter
{
    /// <summary>
    /// Adapts a visitor message of <paramref name="session"/> into an inbound message attributed to the
    /// session's visitor address (<c>webchat-&lt;guid&gt;</c>), never to the session id: the session id is a
    /// transport handle, and the visitor address is the contact address the session was created with
    /// (spec <c>webchat-visitor-identity</c>, design D16).
    /// </summary>
    public static InboundMessage ToInboundMessage(
        WebChatSession session,
        MessageEnvelope content,
        string externalMessageId,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(session);
        var from = new ChannelAddress(ChannelType.WebChat, session.CustomerAddress.Address);
        return new InboundMessage(from, content, externalMessageId, timestamp);
    }
}
