using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Services;

public interface IConversationService
{
    Task<Message> SendMessageAsync(
        EntityId conversationId,
        TenantId tenantId,
        MessageEnvelope envelope,
        EntityId senderId,
        ConversationOwnerKind senderKind,
        CancellationToken ct);

    /// <summary>
    /// Sends like <see cref="SendMessageAsync"/>, but a channel rule that forbids the send is answered as a typed
    /// <see cref="MessageSendOutcome.Refused"/> instead of a stored failed message: nothing is stored and no
    /// request reaches the provider. Today the rule is WhatsApp's 24-hour customer-service window — outside it, a
    /// send without <paramref name="templateId"/> is refused with <c>whatsapp-template-required</c>.
    /// </summary>
    Task<MessageSendOutcome> TrySendMessageAsync(
        EntityId conversationId,
        TenantId tenantId,
        MessageEnvelope envelope,
        EntityId senderId,
        ConversationOwnerKind senderKind,
        string? templateId,
        CancellationToken ct);

    Task<Conversation> GetOrCreateForContactAsync(
        TenantId tenantId,
        EntityId contactId,
        ChannelType channel,
        CancellationToken ct);
}
