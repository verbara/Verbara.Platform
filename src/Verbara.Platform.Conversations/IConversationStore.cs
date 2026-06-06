using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public interface IConversationStore
{
    Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
    Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct);
    Task SaveAsync(Conversation conversation, CancellationToken ct);
    Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct);

    /// <summary>
    /// Returns the voice conversation correlated to an Asterisk call <paramref name="voiceLinkedId"/>,
    /// or <see langword="null"/> if none exists. The per-call idempotency lookup the voice bridge
    /// uses to avoid creating a duplicate Conversation for the same physical call.
    /// </summary>
    Task<Conversation?> FindByVoiceLinkedIdAsync(TenantId tenantId, string voiceLinkedId, CancellationToken ct);

    /// <summary>
    /// Returns the voice conversation correlated to an Asterisk call <paramref name="voiceLinkedId"/>
    /// WITHOUT a tenant scope, or <see langword="null"/> if none exists. An Asterisk <c>LinkedId</c>
    /// is globally unique per call on a given Asterisk, so this resolves at most one row. Used ONLY
    /// by the voice bridge's hangup handler to recover the tenant of a tracked call when a leadership
    /// failover left this pod's in-memory <c>CallSession.TenantId</c> unstamped (and the trunk channel
    /// is already gone, so AMI re-resolution is impossible) — the losslessness guarantee on failover.
    /// </summary>
    Task<Conversation?> FindByVoiceLinkedIdAcrossTenantsAsync(string voiceLinkedId, CancellationToken ct);

    /// <summary>Returns all conversations for a given contact (GDPR export).</summary>
    Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);

    /// <summary>Deletes all conversations for a contact and returns the count deleted (GDPR purge).</summary>
    Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);

    /// <summary>Deletes conversations older than cutoff and returns the count deleted (retention policy).</summary>
    Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct);

    /// <summary>Returns conversations in Queued state ordered by CreatedAt ASC (FIFO).</summary>
    Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct);

    /// <summary>Returns conversations in a specific state ordered by CreatedAt ASC.</summary>
    Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct);

    /// <summary>
    /// W4 — count of conversations the agent actively OWNS in engaged states
    /// (ConversationStateMachine.ActiveWorkStates). Excludes parked/pre-accept/terminal.
    /// Covers voice + digital. Used by the deferred-pause drain to decide when a
    /// pending pause may apply.
    /// </summary>
    Task<int> CountActiveWorkAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
}
