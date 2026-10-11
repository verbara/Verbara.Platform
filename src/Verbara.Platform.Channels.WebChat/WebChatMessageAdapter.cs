using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat;

public sealed class WebChatMessageAdapter
{
    public static InboundMessage ToInboundMessage(
        string sessionId,
        MessageEnvelope content,
        string externalMessageId,
        DateTimeOffset timestamp)
    {
        var from = new ChannelAddress(ChannelType.WebChat, sessionId);
        return new InboundMessage(from, content, externalMessageId, timestamp);
    }

    /// <summary>
    /// Adapts a visitor message of <paramref name="session"/> into an inbound message.
    /// </summary>
    /// <remarks>
    /// RED-PHASE STUB (whatsapp-works-for-real 8.2): keeps today's behaviour — <c>From</c> is the
    /// session id — so <c>ToInboundMessage_ShouldUseVisitorAddress_NotSessionId</c> fails for the
    /// stated reason. Task 8.3 makes <c>From</c> the session's visitor address (design D16).
    /// </remarks>
    public static InboundMessage ToInboundMessage(
        WebChatSession session,
        MessageEnvelope content,
        string externalMessageId,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ToInboundMessage(session.SessionId, content, externalMessageId, timestamp);
    }
}
