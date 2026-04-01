using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public interface IConversationStore
{
    Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
    Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct);
    Task SaveAsync(Conversation conversation, CancellationToken ct);
    Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct);

    /// <summary>Returns all conversations for a given contact (GDPR export).</summary>
    Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);

    /// <summary>Deletes all conversations for a contact and returns the count deleted (GDPR purge).</summary>
    Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);

    /// <summary>Deletes conversations older than cutoff and returns the count deleted (retention policy).</summary>
    Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct);
}
