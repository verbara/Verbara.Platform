using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Queues;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Conversation> _items = new();
    private readonly InMemoryMessageStore? _messages;
    private readonly InMemoryContactSummarySources? _summarySources;

    /// <summary>Creates the store, with the stores its contact summaries and message checks read.</summary>
    /// <param name="messages">
    /// The message store <see cref="DeleteIfNoMessagesAsync"/> and <see cref="ListContactSummariesAsync"/> consult; a
    /// store built without one cannot answer them.
    /// </param>
    /// <param name="summarySources">
    /// The stores <see cref="ListContactSummariesAsync"/> joins (queue names, agent names, dispositions); a store built
    /// without them cannot answer it.
    /// </param>
    public InMemoryConversationStore(
        InMemoryMessageStore? messages = null, InMemoryContactSummarySources? summarySources = null)
    {
        _messages = messages;
        _summarySources = summarySources;
    }

    public Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, conversationId), out var item);
        return Task.FromResult(item);
    }

    public Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(c => c.TenantId == tenantId)
            .Where(c => query.State == null || c.State == query.State)
            .Where(c => query.ContactId == null || c.ContactId == query.ContactId)
            .Where(c => query.CaseId == null || c.CaseId == query.CaseId)
            // contact-history-opens-by-assignment (design D4): kind and id together, as the Postgres filter.
            .Where(c => query.AssignedAgentId == null
                || (c.Owner is { Kind: ConversationOwnerKind.Agent } owner && owner.OwnerId == query.AssignedAgentId))
            // contact-history-opens-by-assignment (design D4): owned by the agent in any state, or an open offer to it.
            .Where(c => query.VisibleToAgentId is not { } visibleTo
                || (c.Owner is { Kind: ConversationOwnerKind.Agent } visibleOwner && visibleOwner.OwnerId == visibleTo)
                || (c.State == ConversationState.Offered
                    && c.Metadata.TryGetValue(ConversationQuery.OfferedToMetadataKey, out var offeredTo)
                    && string.Equals(offeredTo, visibleTo.Value, StringComparison.Ordinal)))
            .Where(c => query.Channel == null || c.Channel == query.Channel)
            .ToList();

        var totalCount = filtered.Count;
        var offset = (query.Page - 1) * query.PageSize;
        var items = filtered.Skip(offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<Conversation>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(Conversation conversation, CancellationToken ct)
    {
        // Mirror the Postgres partial-unique index uq_conversations_voice_linked_id:
        // a second conversation with the same (tenant, voice_linked_id) but a DIFFERENT
        // conversation_id is rejected as already-tracked (no-op), so InMemory and Postgres
        // agree on per-call voice idempotency (Phase-2.1 parity lesson). A re-save of the
        // SAME conversation_id is a normal upsert.
        if (conversation.VoiceLinkedId is { } linkedId)
        {
            var existing = _items.Values.FirstOrDefault(c =>
                c.TenantId == conversation.TenantId &&
                c.VoiceLinkedId == linkedId &&
                c.ConversationId != conversation.ConversationId);
            if (existing is not null)
                return Task.CompletedTask;
        }

        // conversations-close-after-inactivity (design D2): the insert starts the clock (the creation, or the send of a
        // conversation born with a message); an update never writes it, so a stale copy cannot roll it back. Mirrors
        // SaveSql's INSERT-only COALESCE(@LastMessageAt, @CreatedAt).
        var key = (conversation.TenantId, conversation.ConversationId);
        var exists = _items.TryGetValue(key, out var stored);
        conversation.LastMessageAt = exists
            ? stored!.LastMessageAt
            : conversation.LastMessageAt ?? conversation.CreatedAt;

        // contact-history-opens-by-assignment (design D7): the store, never the caller, writes the last queue. A queue
        // owner names its queue; any other owner keeps the stored one (none on an insert). Mirrors SaveSql's
        // COALESCE(QueueIdWriteCase, conversations.queue_id).
        conversation.QueueId = conversation.Owner is { Kind: ConversationOwnerKind.Queue, OwnerId: { } queueId }
            ? queueId
            : exists ? stored!.QueueId : null;

        _items[key] = conversation;
        return Task.CompletedTask;
    }

    public Task<Conversation?> FindOpenBySessionAddressAsync(TenantId tenantId, EntityId contactId, ChannelAddress sessionAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionAddress);
        var result = _items.Values
            .Where(c => c.TenantId == tenantId
                && c.ContactId == contactId
                && c.Channel == ChannelType.WebChat
                && !ConversationStateMachine.IsTerminal(c.State)
                && c.Metadata.TryGetValue(WebChatConversationMetadata.SessionAddressKey, out var recorded)
                && recorded == sessionAddress.Address)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefault();

        return Task.FromResult(result);
    }

    public Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(c =>
            c.TenantId == tenantId &&
            c.ContactId == contactId &&
            c.Channel == channel &&
            !ConversationStateMachine.IsTerminal(c.State));

        return Task.FromResult(result);
    }

    public Task<int> CountOpenByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) =>
        Task.FromResult(_items.Values.Count(c =>
            c.TenantId == tenantId &&
            c.ContactId == contactId &&
            c.Channel == channel &&
            !ConversationStateMachine.IsTerminal(c.State)));

    public Task<DateTimeOffset?> FindLastInboundAtAsync(
        TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
    {
        var conversationIds = _items.Values
            .Where(c => c.TenantId == tenantId && c.ContactId == contactId && c.Channel == channel)
            .Select(c => c.ConversationId)
            .ToHashSet();

        return Task.FromResult(_messages?.LastInboundAt(tenantId, conversationIds));
    }

    public Task<Conversation?> FindByVoiceLinkedIdAsync(TenantId tenantId, string voiceLinkedId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(c =>
            c.TenantId == tenantId &&
            c.VoiceLinkedId == voiceLinkedId);

        return Task.FromResult(result);
    }

    public Task<Conversation?> FindByVoiceLinkedIdAcrossTenantsAsync(string voiceLinkedId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(c => c.VoiceLinkedId == voiceLinkedId);
        return Task.FromResult(result);
    }

    public Task<Conversation?> FindByIdAcrossTenantsAsync(EntityId conversationId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(c => c.ConversationId == conversationId);
        return Task.FromResult(result);
    }

    public async Task<PagedResult<ContactConversationSummary>> ListContactSummariesAsync(
        TenantId tenantId, EntityId contactId, EntityId? ownedByAgentId, int page, int pageSize, CancellationToken ct)
    {
        // contact-history-opens-by-assignment (design D5, D6, D18): mirrors the Postgres summary query — every state,
        // created_at then the id descending (ordinal), the owner restriction on kind and id together, then the joins.
        var messages = _messages ?? throw new InvalidOperationException(
            "This conversation store was built without a message store, so it cannot read a contact's summaries.");
        var sources = _summarySources ?? throw new InvalidOperationException(
            "This conversation store was built without its summary sources, so it cannot read a contact's summaries.");

        var matching = _items.Values
            .Where(c => c.TenantId == tenantId && c.ContactId == contactId)
            .Where(c => ownedByAgentId is not { } owner
                || (c.Owner is { Kind: ConversationOwnerKind.Agent } agentOwner && agentOwner.OwnerId == owner))
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.ConversationId.Value, StringComparer.Ordinal)
            .ToList();

        var items = new List<ContactConversationSummary>();
        foreach (var conversation in matching.Skip((page - 1) * pageSize).Take(pageSize))
        {
            var queueName = conversation.QueueId is { } queueId
                ? (await sources.Queues().GetByIdAsync(tenantId, queueId, ct))?.Name
                : null;
            var agentName = conversation.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } agentId }
                ? (await sources.Agents().GetByIdAsync(tenantId, agentId, ct))?.DisplayName
                : null;

            ConversationDispositionRef? disposition = null;
            if (await sources.Submissions().GetByConversationIdAsync(tenantId, conversation.ConversationId, ct) is { } submission)
            {
                var correction = await sources.Corrections().GetAsync(tenantId, conversation.ConversationId, ct);
                disposition = new ConversationDispositionRef(
                    correction?.CorrectedLeafNodeId ?? submission.LeafNodeId, submission.SchemaId, submission.SchemaVersion);
            }

            var last = messages.NewestActivityMessage(tenantId, conversation.ConversationId);
            items.Add(new ContactConversationSummary(
                conversation,
                queueName,
                agentName,
                disposition,
                last is null ? null : new ConversationActivityMessage(last.Direction, last.Content, last.CreatedAt)));
        }

        return new PagedResult<ContactConversationSummary>(items, matching.Count, page, pageSize);
    }

    public Task<bool> HoldsOpenConversationOfContactAsync(TenantId tenantId, EntityId contactId, EntityId agentId, CancellationToken ct)
    {
        // contact-history-opens-by-assignment (design D2): owner kind and id together, any non-terminal state. An
        // offered conversation is owned by its queue, so an offer is not holding.
        var holds = _items.Values.Any(c =>
            c.TenantId == tenantId &&
            c.ContactId == contactId &&
            c.Owner is { Kind: ConversationOwnerKind.Agent } owner &&
            owner.OwnerId == agentId &&
            !ConversationStateMachine.IsTerminal(c.State));
        return Task.FromResult(holds);
    }

    public Task<EntityId?> FindLastAgentOwnerAsync(TenantId tenantId, EntityId contactId, DateTimeOffset since, CancellationToken ct)
    {
        // conversations-return-to-last-agent (design D4): agent-owned, any state but Merged/Spam, ordered and filtered by
        // the last customer-facing message. LastMessageAt is set on every stored row (the insert's COALESCE above).
        var owner = _items.Values
            .Where(c => c.TenantId == tenantId
                && c.ContactId == contactId
                && c.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: not null }
                && !LastAgentLookup.ExcludedStates.Contains(c.State)
                && c.LastMessageAt is { } at && at >= since)
            .OrderByDescending(c => c.LastMessageAt)
            .Select(c => c.Owner!.OwnerId)
            .FirstOrDefault();
        return Task.FromResult(owner);
    }

    public Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c => c.TenantId == tenantId && c.ContactId == contactId)
            .OrderByDescending(c => c.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        var toDelete = _items
            .Where(kv => kv.Key.Item1 == tenantId && kv.Value.ContactId == contactId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toDelete)
            _items.TryRemove(key, out _);

        return Task.FromResult(toDelete.Count);
    }

    public Task<bool> DeleteIfNoMessagesAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var messages = _messages ?? throw new InvalidOperationException(
            "This conversation store was built without a message store, so it cannot tell whether a conversation is empty.");
        return Task.FromResult(messages.RemoveConversationIfEmpty(
            tenantId, conversationId, () => _items.TryRemove((tenantId, conversationId), out _)));
    }

    // Mirrors the Postgres purge filter: a terminal row counts from its close; a Resolved row (terminal for the purge)
    // from its close when set, else its last change, else its creation, and only with no message at or after the
    // cutoff (a customer reply lands in the message store, not on the row); any other state is live and never past
    // retention. A store built without a message store holds no messages, so the message condition holds trivially.
    private bool IsPastRetention(Conversation conversation, DateTimeOffset cutoff)
    {
        if (ConversationStateMachine.IsTerminal(conversation.State))
            return conversation.ClosedAt < cutoff;

        if (conversation.State != ConversationState.Resolved || (conversation.ClosedAt ?? conversation.UpdatedAt ?? conversation.CreatedAt) >= cutoff)
            return false;

        return _messages?.HasMessageSince(conversation.TenantId, conversation.ConversationId, cutoff) != true;
    }

    public Task<int> DeleteClosedBeforeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        var toDelete = _items
            .Where(kv => kv.Key.Item1 == tenantId && IsPastRetention(kv.Value, cutoff))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toDelete)
            _items.TryRemove(key, out _);

        return Task.FromResult(toDelete.Count);
    }

    public Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            // Only a queue-owned conversation is routable (parity with PostgresConversationStore).
            .Where(c => c.TenantId == tenantId && c.State == ConversationState.Queued
                && c.Owner is { Kind: ConversationOwnerKind.Queue, OwnerId: not null })
            .OrderBy(c => c.QueuePriority)
            .ThenBy(c => c.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c => c.TenantId == tenantId && c.State == state)
            .OrderBy(c => c.QueuePriority)
            .ThenBy(c => c.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<bool> TryConditionalCloseWrapUpAsync(
        TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        // Mirror the Postgres UPDATE ... WHERE state = WrapUp single-row CAS: lock on the stored
        // entity so a concurrent human typify (which transitions it out of WrapUp) and this close
        // cannot interleave. Returns false (0 rows) when the conversation is missing or already
        // left WrapUp — the autonomous caller then skips stamping (ADR-0034 Decision 2).
        if (!_items.TryGetValue((tenantId, conversationId), out var conv))
            return Task.FromResult(false);

        lock (conv)
        {
            if (conv.State != ConversationState.WrapUp)
                return Task.FromResult(false);

            conv.TransitionTo(ConversationState.Closed, now);
            conv.UpdatedAt = now;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryCommitWrapUpAsync(
        TenantId tenantId, EntityId conversationId, EntityId ownerAgentId, DateTimeOffset now, CancellationToken ct)
    {
        // Mirror the Postgres UPDATE ... WHERE state = ANY(@From) AND owner_kind = Agent AND owner_id = @Owner
        // single-row CAS (close-chat-without-typification-licence D3) under the stored entity's lock, as
        // TryConditionalCloseWrapUpAsync does. State is assigned directly, like the SQL: OnHold/Consulting
        // reach WrapUp here without the in-memory step to Active the switchboard performs.
        if (!_items.TryGetValue((tenantId, conversationId), out var conv))
            return Task.FromResult(false);

        lock (conv)
        {
            if (!ConversationStateMachine.IsFailoverWork(conv.State) ||
                conv.Owner is not { Kind: ConversationOwnerKind.Agent } owner ||
                owner.OwnerId != ownerAgentId)
            {
                return Task.FromResult(false);
            }

            conv.State = ConversationState.WrapUp;
            conv.UpdatedAt = now;
            return Task.FromResult(true);
        }
    }

    public Task<int> CountActiveWorkAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        var count = _items.Values.Count(c =>
            c.TenantId == tenantId &&
            c.Owner is { Kind: ConversationOwnerKind.Agent } owner &&
            owner.OwnerId == agentId &&
            ConversationStateMachine.IsActiveWork(c.State));
        return Task.FromResult(count);
    }

    public Task<IReadOnlyList<Conversation>> ListFailoverWorkByOwnerAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c =>
                c.TenantId == tenantId &&
                c.Owner is { Kind: ConversationOwnerKind.Agent } owner &&
                owner.OwnerId == agentId &&
                ConversationStateMachine.IsFailoverWork(c.State))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Conversation>> ListPendingCallbackEvalAsync(CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c =>
                c.State == ConversationState.WrapUp &&
                c.Metadata.TryGetValue("pendingCallbackEval", out var v) && v == "true")
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Conversation>> ListCallbackStuckAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c =>
                c.TenantId == tenantId &&
                c.State == ConversationState.WrapUp &&
                c.Metadata.TryGetValue("callbackStuck", out var v) && v == "true")
            .ToList();
        return Task.FromResult(result);
    }

    // ── conversations-close-after-inactivity (design D3, D4, D7, D8, D9, D12) ─────────────────────────────
    // Each conditional write mirrors its Postgres UPDATE under the stored entity's lock, as TryCommitWrapUpAsync does.
    // State is assigned directly, never through Conversation.TransitionTo (the table has no agent-held → Closed edge).

    public Task<IReadOnlyList<Conversation>> ListInactiveAsync(
        TenantId tenantId, IReadOnlyCollection<ConversationState> states, DateTimeOffset cutoff, int limit, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c => c.TenantId == tenantId && states.Contains(c.State) && c.Channel != ChannelType.Voice
                && c.LastMessageAt < cutoff)
            .OrderBy(c => c.LastMessageAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<InactivityCloseOutcome?> TryCloseInactiveAsync(TenantId tenantId, InactivityCloseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ConversationStateMachine.CanCloseForInactivity(request.ReadState)
            || !_items.TryGetValue((tenantId, request.ConversationId), out var conv))
        {
            return Task.FromResult<InactivityCloseOutcome?>(null);
        }

        lock (conv)
        {
            if (conv.State != request.ReadState
                || conv.Channel == ChannelType.Voice
                || conv.Owner?.Kind != request.ReadOwner?.Kind
                || conv.Owner?.OwnerId != request.ReadOwner?.OwnerId
                || conv.LastMessageAt is not { } clock
                || clock >= request.Cutoff)
            {
                return Task.FromResult<InactivityCloseOutcome?>(null);
            }

            var reason = clock < request.BacklogCutoff
                ? ConversationCloseReasons.InactivityBacklog
                : ConversationCloseReasons.Inactivity;
            conv.State = ConversationState.Closed;
            conv.ClosedAt ??= request.Now;
            conv.UpdatedAt = request.Now;
            conv.SetMetadata(ConversationCloseReasons.MetadataKey, reason);
            return Task.FromResult<InactivityCloseOutcome?>(new InactivityCloseOutcome(reason, clock));
        }
    }

    public Task<bool> TryClaimForInboundAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        if (!_items.TryGetValue((tenantId, conversationId), out var conv))
            return Task.FromResult(false);

        lock (conv)
        {
            if (ConversationStateMachine.IsTerminal(conv.State) || conv.State == ConversationState.Resolved)
                return Task.FromResult(false);

            AdvanceClock(conv, now);
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryCloseResolvedForReplyAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        if (!_items.TryGetValue((tenantId, conversationId), out var conv))
            return Task.FromResult(false);

        lock (conv)
        {
            if (conv.State != ConversationState.Resolved)
                return Task.FromResult(false);

            conv.State = ConversationState.Closed;
            conv.ClosedAt = now;
            conv.UpdatedAt = now;
            conv.SetMetadata(ConversationCloseReasons.MetadataKey, ConversationCloseReasons.CustomerReturned);
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryClaimForSendAsync(
        TenantId tenantId, EntityId conversationId, DateTimeOffset now, bool advanceClock, CancellationToken ct)
    {
        if (!_items.TryGetValue((tenantId, conversationId), out var conv))
            return Task.FromResult(false);

        lock (conv)
        {
            if (!ConversationStateMachine.IsFailoverWork(conv.State))
                return Task.FromResult(false);

            if (advanceClock)
                AdvanceClock(conv, now);
            return Task.FromResult(true);
        }
    }

    /// <summary>Advances each open, non-voice conversation's clock of the tenant to its newest stored activity message.</summary>
    /// <remarks>
    /// Mirrors the Postgres statement: the newest activity message per conversation (the message store's
    /// <see cref="InMemoryMessageStore.NewestActivityByConversation"/>), applied under each row's lock to the
    /// non-terminal, non-voice rows of the tenant whose clock is older. A store built without a message store holds no
    /// messages, so it has nothing to reconcile.
    /// </remarks>
    public Task<int> ReconcileLastMessageAtAsync(TenantId tenantId, CancellationToken ct)
    {
        if (_messages is null)
            return Task.FromResult(0);

        var newest = _messages.NewestActivityByConversation(tenantId);
        var advanced = 0;
        var candidates = _items.Values
            .Where(c => c.TenantId == tenantId && c.Channel != ChannelType.Voice && newest.ContainsKey(c.ConversationId));
        foreach (var conv in candidates)
        {
            var at = newest[conv.ConversationId];
            lock (conv)
            {
                if (ConversationStateMachine.IsTerminal(conv.State) || conv.LastMessageAt is { } clock && clock >= at)
                    continue;

                conv.LastMessageAt = at;
                advanced++;
            }
        }

        return Task.FromResult(advanced);
    }

    // GREATEST(COALESCE(last_message_at, '-infinity'), @Now): the clock never moves back.
    private static void AdvanceClock(Conversation conv, DateTimeOffset now)
    {
        if (conv.LastMessageAt is not { } clock || clock < now)
            conv.LastMessageAt = now;
    }
}
