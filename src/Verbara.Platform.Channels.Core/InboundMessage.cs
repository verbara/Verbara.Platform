using Verbara.Platform.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.Core;

public sealed record InboundMessage(
    ChannelAddress From,
    MessageEnvelope Content,
    string ExternalMessageId,
    DateTimeOffset Timestamp);
