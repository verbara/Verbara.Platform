using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.WebChat;

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
