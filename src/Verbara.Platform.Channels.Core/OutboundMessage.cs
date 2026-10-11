using Verbara.Platform.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.Core;

/// <summary>A message for a channel connector to deliver.</summary>
/// <param name="To">The recipient's address on the channel.</param>
/// <param name="Content">The message body; ignored by a connector that sends a template.</param>
/// <param name="TenantId">The tenant whose channel configuration sends it.</param>
/// <param name="ConversationId">The conversation the message belongs to.</param>
/// <param name="TemplateId">The provider template to send instead of <paramref name="Content"/>, if any.</param>
/// <param name="TemplateLanguage">The template's language code (e.g. <c>es_MX</c>); the connector's default when null.</param>
public sealed record OutboundMessage(
    ChannelAddress To,
    MessageEnvelope Content,
    TenantId TenantId,
    EntityId ConversationId,
    string? TemplateId = null,
    string? TemplateLanguage = null);
