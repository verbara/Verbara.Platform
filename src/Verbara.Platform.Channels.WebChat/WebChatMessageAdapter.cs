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
}
