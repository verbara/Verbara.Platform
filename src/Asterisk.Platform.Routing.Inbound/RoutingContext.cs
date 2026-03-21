using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Routing.Inbound;

public sealed record RoutingContext(
    Conversation Conversation,
    Contact Contact,
    ChannelType Channel,
    MessageEnvelope? InitialMessage,
    TenantId TenantId);
