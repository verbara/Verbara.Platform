using Verbara.Platform.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.Core;

public sealed record OutboundMessage(
    ChannelAddress To,
    MessageEnvelope Content,
    TenantId TenantId,
    EntityId ConversationId,
    string? TemplateId = null);
