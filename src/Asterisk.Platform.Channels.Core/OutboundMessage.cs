using Asterisk.Platform.Core;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Channels.Core;

public sealed record OutboundMessage(
    ChannelAddress To,
    MessageEnvelope Content,
    TenantId TenantId,
    EntityId ConversationId,
    string? TemplateId = null);
