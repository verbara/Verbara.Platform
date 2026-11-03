using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public interface IConversationStore
{
    Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
    Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct);
    Task SaveAsync(Conversation conversation, CancellationToken ct);
    Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct);

    /// <summary>
    /// When the contact last wrote to the tenant on <paramref name="channel"/>: the creation time of the most recent
    /// inbound message in <b>any</b> of the contact's conversations on that channel, or <see langword="null"/> when
    /// there is none. WhatsApp's customer-service window belongs to the user, not to one conversation, so an agent
    /// starting a new conversation reads it here (agent-started-conversations, design D14).
    /// </summary>
    Task<DateTimeOffset?> FindLastInboundAtAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct);

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

    /// <summary>
    /// Deletes the conversation only while no message belongs to it, and returns whether it was deleted. The
    /// inbound pipeline calls it for a conversation it has just created when its message insert lost the race
    /// to an identical delivery: that conversation never received a message, and left behind it would be routed
    /// as a queued, owner-less conversation. A conversation another delivery has already put a message in is kept.
    /// </summary>
    Task<bool> DeleteIfNoMessagesAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);

    /// <summary>
    /// Retention purge: deletes the tenant's conversations in a terminal state (Closed, Abandoned, Merged, Spam —
    /// <see cref="ConversationStateMachine.IsTerminal"/>) whose <see cref="Conversation.ClosedAt"/> is before
    /// <paramref name="cutoff"/>, plus the <see cref="ConversationState.Resolved"/> ones, which count as terminal for the
    /// purge: their clock is <see cref="Conversation.ClosedAt"/> when set, else the last change
    /// (<see cref="Conversation.UpdatedAt"/>, else <see cref="Conversation.CreatedAt"/>), and they also need no message at
    /// or after the cutoff, since nothing closes a Resolved conversation on its own and a customer reply is stored as
    /// a message without touching the conversation — and returns the count deleted. A live conversation (any other state) is
    /// never deleted, however old. Messages are not touched here — the caller removes the orphans after.
    /// </summary>
    Task<int> DeleteClosedBeforeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct);

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
    /// State-and-owner compare-and-set that moves an agent-held conversation into
    /// <see cref="ConversationState.WrapUp"/> (close-chat-without-typification-licence D3): transitions
    /// <paramref name="conversationId"/> <b>only if</b> its state is one of
    /// <see cref="ConversationStateMachine.FailoverWorkStates"/> (<c>Active</c>, <c>OnHold</c>,
    /// <c>Consulting</c>) <b>and</b> it is still owned by the agent <paramref name="ownerAgentId"/>, stamping
    /// <c>updated_at</c> = <paramref name="now"/>. The owner is kept. Returns <see langword="true"/> when this
    /// caller won (and so is the one that must release the owner's capacity unit), <see langword="false"/>
    /// when zero rows matched (missing, already left the agent-held states, or transferred to another owner).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner predicate matters because a transfer keeps <c>Active</c> and swaps the owner: a state-only
    /// predicate would let the old owner's wrap-up win after a committed transfer, releasing that owner
    /// twice and leaking the new owner's unit. Concurrent wrap-ups and closes therefore release once.
    /// </para>
    /// <para>
    /// Known gap (design D12, Risk 2): the other ownership moves (transfer, requeue, return-to-bot, hold)
    /// still save the whole row without a state predicate. One that loaded the row as agent-held
    /// <b>before</b> this update commits can, after it, release the same owner again and/or write an
    /// agent-held state back over <c>WrapUp</c>, which a later close can win a second time. Exactly-once is
    /// not guaranteed against that interleaving until those moves become compare-and-set too.
    /// </para>
    /// </remarks>
    Task<bool> TryCommitWrapUpAsync(
        TenantId tenantId, EntityId conversationId, EntityId ownerAgentId, DateTimeOffset now, CancellationToken ct);

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

    // ── conversations-close-after-inactivity (design D3, D4, D7, D8, D9, D12) ─────────────────────────────

    /// <summary>
    /// The tenant's idle digital conversations: <paramref name="states"/> only, never voice, with a clock
    /// (<see cref="Conversation.LastMessageAt"/>) strictly older than <paramref name="cutoff"/>, oldest clock first, at
    /// most <paramref name="limit"/>. A range on <c>idx_conversations_state_last_message</c> (no fallback on the clock).
    /// </summary>
    Task<IReadOnlyList<Conversation>> ListInactiveAsync(
        TenantId tenantId, IReadOnlyCollection<ConversationState> states, DateTimeOffset cutoff, int limit, CancellationToken ct);

    /// <summary>
    /// The conditional inactivity close (design D4): one write that moves the row to <c>Closed</c> (stamping
    /// <c>closed_at</c> when unset and <c>updated_at</c>, and recording the reason in <c>_closedReason</c>, keeping the
    /// owner) <b>only if</b> its state is still <see cref="InactivityCloseRequest.ReadState"/>, its owner still
    /// <see cref="InactivityCloseRequest.ReadOwner"/> and its clock still older than the cutoff. The reason is decided
    /// in the write: <c>inactivity-backlog</c> when the clock is older than the backlog cutoff, else <c>inactivity</c>
    /// (D12). Returns the outcome when this caller won, <see langword="null"/> when it lost, and also, writing nothing,
    /// when the read state fails <see cref="ConversationStateMachine.CanCloseForInactivity"/> (D3). Never goes through
    /// <see cref="Conversation.TransitionTo"/>: the transition table has no agent-held <c>→ Closed</c> edge.
    /// </summary>
    Task<InactivityCloseOutcome?> TryCloseInactiveAsync(TenantId tenantId, InactivityCloseRequest request, CancellationToken ct);

    /// <summary>
    /// The inbound claim (design D7): advances the clock to <paramref name="now"/> (the platform's clock, never the
    /// provider's), monotonically, on a row that is neither terminal nor <c>Resolved</c>. Returns
    /// <see langword="false"/> when no such row matched (closed, purged, deleted or <c>Resolved</c> since the read):
    /// the caller resolves again. Does not touch <c>updated_at</c>.
    /// </summary>
    Task<bool> TryClaimForInboundAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// The return close (design D8): <c>Resolved → Closed</c> with <c>closed_at</c> = <paramref name="now"/> and
    /// <c>_closedReason</c> = <c>customer-returned</c>, the owner kept, only if the row is still <c>Resolved</c>.
    /// Returns whether this caller won it.
    /// </summary>
    Task<bool> TryCloseResolvedForReplyAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// The send claim (design D9): matches only an <c>Active</c>, <c>OnHold</c> or <c>Consulting</c> row, and advances
    /// its clock to <paramref name="now"/> (monotonically) only when <paramref name="advanceClock"/> is set (a message
    /// stored <c>Pending</c> for a connector; a refused send never advances it). Returns whether the row matched.
    /// </summary>
    Task<bool> TryClaimForSendAsync(
        TenantId tenantId, EntityId conversationId, DateTimeOffset now, bool advanceClock, CancellationToken ct);

    /// <summary>
    /// The clock reconciliation (design D2): for the tenant's non-terminal digital rows, advances
    /// <see cref="Conversation.LastMessageAt"/> to the newest stored <b>activity message</b> (an <c>Inbound</c> message, or
    /// an <c>Outbound</c> one not stored <c>Failed</c>; never a <c>System</c> message) at its <c>created_at</c>, only
    /// where that is newer than the clock. Monotonic and idempotent: it never moves a clock back, and a second run
    /// changes nothing. Covers the messages an older binary stored without advancing the clock (a rollback, then a
    /// re-upgrade). Returns how many rows it advanced.
    /// </summary>
    Task<int> ReconcileLastMessageAtAsync(TenantId tenantId, CancellationToken ct);
}
