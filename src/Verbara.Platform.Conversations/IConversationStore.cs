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

    /// <summary>
    /// Returns the conversation with <paramref name="conversationId"/> WITHOUT a tenant scope, or
    /// <see langword="null"/> if none exists. A conversation id is a globally unique <c>EntityId</c>,
    /// so this resolves at most one row. Used ONLY by the in-process voice-CSAT capture sink
    /// (csat-completion): the Pro <c>CsatCapture</c> the sink receives carries the correlated
    /// conversation id but no tenant, so the sink recovers the row's tenant partition via this lookup.
    /// </summary>
    Task<Conversation?> FindByIdAcrossTenantsAsync(EntityId conversationId, CancellationToken ct);

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
    /// State-based compare-and-set close used by the autonomous AI disposition path (ADR-0034
    /// Decision 6): atomically transitions <paramref name="conversationId"/> to
    /// <see cref="ConversationState.Closed"/> <b>only if it is still in
    /// <see cref="ConversationState.WrapUp"/></b>, stamping <c>updated_at</c> = <c>closed_at</c> =
    /// <paramref name="now"/>. Returns <see langword="true"/> when the row was transitioned
    /// (this caller won the race), <see langword="false"/> when zero rows matched (a human already
    /// typified/closed it concurrently, so the caller MUST NOT stamp a disposition).
    /// </summary>
    /// <remarks>
    /// This is the CAS gate that lets a concurrent human typify win: the conditional predicate is
    /// <c>WHERE conversation_id = @id AND tenant_id = @t AND state = WrapUp</c>. The autonomous
    /// submission insert follows only on a <see langword="true"/> result; it is intentionally NOT in
    /// the same DB transaction (a crash between the two leaves a blank Closed = today's behaviour).
    /// </remarks>
    Task<bool> TryConditionalCloseWrapUpAsync(
        TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// W4 — count of conversations the agent actively OWNS in engaged states
    /// (ConversationStateMachine.ActiveWorkStates). Excludes parked/pre-accept/terminal.
    /// Covers voice + digital. Used by the deferred-pause drain to decide when a
    /// pending pause may apply.
    /// </summary>
    Task<int> CountActiveWorkAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);

    /// <summary>
    /// W5 — conversations the agent OWNS in FailoverWorkStates {Active,OnHold,Consulting}
    /// (excludes WrapUp/parked/pre-accept). Returns the full Conversation entities so the
    /// failover sweep can re-queue them. Used when the owner has gone Offline past the grace.
    /// </summary>
    Task<IReadOnlyList<Conversation>> ListFailoverWorkByOwnerAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);

    /// <summary>
    /// W5b — cross-tenant list of answered voice conversations parked in WrapUp awaiting a
    /// callback-rescue decision (metadata <c>pendingCallbackEval == "true"</c>, stamped by the
    /// voice bridge on an answered-call hangup). The leader-gated CallbackRescueWorker drains
    /// this set after the per-tenant grace. Self-bounds: the worker clears the marker once it
    /// enqueues a callback or escalates.
    /// </summary>
    Task<IReadOnlyList<Conversation>> ListPendingCallbackEvalAsync(CancellationToken ct);

    /// <summary>
    /// W5b — tenant-scoped list of voice conversations whose rescue callbacks exhausted the
    /// retry cap (in WrapUp, metadata <c>callbackStuck == "true"</c>). Surfaced in the supervisor
    /// stuck-work view so a human can re-arm the rescue or close the call.
    /// </summary>
    Task<IReadOnlyList<Conversation>> ListCallbackStuckAsync(TenantId tenantId, CancellationToken ct);
}
