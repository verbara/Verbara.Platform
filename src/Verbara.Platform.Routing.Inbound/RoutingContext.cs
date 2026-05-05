using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Routing.Inbound;

public sealed record RoutingContext(
    Conversation Conversation,
    Contact Contact,
    ChannelType Channel,
    MessageEnvelope? InitialMessage,
    TenantId TenantId);
