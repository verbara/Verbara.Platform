using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat;

public sealed class WebChatMessageAdapter
{
    /// <summary>
    /// Adapts a visitor message of <paramref name="session"/> into an inbound message from the session's visitor
    /// address (<c>webchat-&lt;guid&gt;</c>), never from the session id: the session id is a transport handle. It carries
    /// the session's current contact and conversation, so the pipeline keeps the message to them
    /// (spec <c>webchat-visitor-identity</c>).
    /// </summary>
    public static InboundMessage ToInboundMessage(
        WebChatSession session,
        MessageEnvelope content,
        string externalMessageId,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(session);
        var from = new ChannelAddress(ChannelType.WebChat, session.CustomerAddress.Address);
        return new InboundMessage(from, content, externalMessageId, timestamp)
        {
            SessionBinding = new InboundSessionBinding(session.ContactId, session.ConversationId, from),
        };
    }
}
