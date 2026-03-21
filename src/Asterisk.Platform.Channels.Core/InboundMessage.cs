using Asterisk.Platform.Core;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Channels.Core;

public sealed record InboundMessage(
    ChannelAddress From,
    MessageEnvelope Content,
    string ExternalMessageId,
    DateTimeOffset Timestamp);
