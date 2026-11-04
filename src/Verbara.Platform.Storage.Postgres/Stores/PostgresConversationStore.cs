using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Conversations;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresConversationStore : IConversationStore
{
    // Bound as integer[] for the retention purge; derived from the state machine so the two cannot drift.
    private static readonly int[] s_terminalStateValues = Enum.GetValues<ConversationState>()
        .Where(ConversationStateMachine.IsTerminal)
        .Select(state => (int)state)
        .ToArray();

    /// <summary>
    /// conversations-close-after-inactivity (design D2): which stored messages count as customer-facing activity —
    /// an <c>Inbound</c> message (direction 0), or an <c>Outbound</c> one (1) not stored <c>Failed</c> (3); a send
    /// refused before it reached a connector, and every <c>System</c> message, never count. One SQL fragment over a
    /// <c>messages m</c> alias: migration 026's backfill, the clock reconcile and the upgrade note's dry-run repeat it
    /// verbatim.
    /// </summary>
    internal const string ActivityMessagePredicate =
        "(m.direction = 0 OR (m.direction = 1 AND m.delivery_status <> 3))";

    /// <summary>
    /// contact-history-opens-by-assignment (design D7, migration 027): the value of <c>queue_id</c> a statement that
    /// writes the owner must store — the owner's id when the owner is a queue (<c>owner_kind = 3</c>), else NULL. The
    /// whole-row upsert inserts it and, on update, keeps the stored value when it is NULL
    /// (<c>COALESCE(…, conversations.queue_id)</c>), so the column names the last queue that owned the conversation
    /// and never clears. Every statement that writes <c>owner_kind</c> must carry this constant (the guard test of task
    /// 1.6); it reads the statement's <c>@OwnerKind</c> and <c>@OwnerId</c> parameters, as <see cref="BindSave"/> binds them.
    /// </summary>
    internal const string QueueIdWriteCase = "CASE WHEN @OwnerKind = 3 THEN @OwnerId END";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresConversationStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations WHERE tenant_id = @TenantId AND conversation_id = @ConversationId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", conversationId.Value));
            },
            ConversationRow.Map, ct);
        return row?.ToConversation();
    }

    public async Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct)
    {
        var whereClauses = new List<string> { "tenant_id = @TenantId" };
        var binders = new List<Action<NpgsqlParameterCollection>>
        {
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
        };

        if (query.State.HasValue)
        {
            whereClauses.Add("state = @State");
            binders.Add(p => p.Add(new NpgsqlParameter("State", (int)query.State.Value)));
        }
        if (query.ContactId.HasValue)
        {
            whereClauses.Add("contact_id = @ContactId");
            binders.Add(p => p.Add(new NpgsqlParameter("ContactId", query.ContactId.Value.Value)));
        }
        if (query.CaseId.HasValue)
        {
            whereClauses.Add("case_id = @CaseId");
            binders.Add(p => p.Add(new NpgsqlParameter("CaseId", query.CaseId.Value.Value)));
        }
        if (query.AssignedAgentId.HasValue)
        {
            // contact-history-opens-by-assignment (design D4): the owner is an agent, decided on kind and id together,
            // so a queue or a bot whose id equals the agent's does not own the agent's conversations.
            whereClauses.Add("owner_kind = @AssignedOwnerKind AND owner_id = @OwnerId");
            binders.Add(p => p.Add(new NpgsqlParameter("AssignedOwnerKind", NpgsqlDbType.Integer) { Value = (int)ConversationOwnerKind.Agent }));
            binders.Add(p => p.Add(new NpgsqlParameter("OwnerId", query.AssignedAgentId.Value.Value)));
        }
        if (query.VisibleToAgentId.HasValue)
        {
            // contact-history-opens-by-assignment (design D4): owned by the agent in any state, or an open offer to it.
            whereClauses.Add(
                "((owner_kind = @VisibleOwnerKind AND owner_id = @VisibleToAgentId) " +
                $"OR (state = @OfferedState AND metadata->>'{ConversationQuery.OfferedToMetadataKey}' = @VisibleToAgentId))");
            binders.Add(p => p.Add(new NpgsqlParameter("VisibleOwnerKind", NpgsqlDbType.Integer) { Value = (int)ConversationOwnerKind.Agent }));
            binders.Add(p => p.Add(new NpgsqlParameter("OfferedState", NpgsqlDbType.Integer) { Value = (int)ConversationState.Offered }));
            binders.Add(p => p.Add(new NpgsqlParameter("VisibleToAgentId", NpgsqlDbType.Text) { Value = query.VisibleToAgentId.Value.Value }));
        }
        if (query.Channel.HasValue)
        {
            whereClauses.Add("channel = @Channel");
            binders.Add(p => p.Add(new NpgsqlParameter("Channel", (int)query.Channel.Value)));
        }

        var where = string.Join(" AND ", whereClauses);
        var offset = (query.Page - 1) * query.PageSize;

        void BindFilters(NpgsqlParameterCollection p) { foreach (var b in binders) b(p); }

        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            $"SELECT COUNT(*) FROM conversations WHERE {where}", BindFilters, ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            $"metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            $"FROM conversations WHERE {where} ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            p =>
            {
                BindFilters(p);
                p.Add(new NpgsqlParameter("Limit", query.PageSize));
                p.Add(new NpgsqlParameter("Offset", offset));
            },
            ConversationRow.Map, ct);

        var items = rows.Select(r => r.ToConversation()).ToList();
        return new PagedResult<Conversation>(items, total, query.Page, query.PageSize);
    }

    // licensed-agent-metering (design D5) — one SQL text and one binder, run standalone here and inside the
    // licensed-agent writer's transaction by the overload below (an ownership change and its ledger row).
    private const string SaveSql =
        "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
        "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, " +
        "queue_id) " +
        "VALUES (@ConversationId, @TenantId, @ContactId, @Channel, @State, @OwnerKind, @OwnerId, @CaseId, " +
        "@Metadata::jsonb, @CreatedAt, @ClosedAt, @UpdatedAt, @CreatedBy, @UpdatedBy, @VoiceLinkedId, @QueuePriority, " +
        // conversations-close-after-inactivity (design D2): the INSERT starts the clock (the send of a conversation born
        // with a message, else the creation); the DO UPDATE below never names it, so a whole-row save of a row loaded
        // before a message arrived cannot roll it back. Only the claim statements move it.
        "COALESCE(@LastMessageAt, @CreatedAt), " +
        // contact-history-opens-by-assignment (design D7): the store, never the caller, writes the last queue.
        QueueIdWriteCase + ") " +
        "ON CONFLICT (tenant_id, conversation_id) DO UPDATE SET " +
        "  state = EXCLUDED.state, owner_kind = EXCLUDED.owner_kind, owner_id = EXCLUDED.owner_id, " +
        "  case_id = EXCLUDED.case_id, metadata = EXCLUDED.metadata, closed_at = EXCLUDED.closed_at, " +
        "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by, " +
        // 3B.2d: an outbound voice Conversation is pre-created with a NULL voice_linked_id; the
        // bridge stamps the real LinkedId later, so it must persist on UPDATE (not just INSERT).
        "  voice_linked_id = EXCLUDED.voice_linked_id, " +
        // W5: a failover re-queue (priority -1) or a normal transfer (priority 0) must persist on UPDATE.
        "  queue_priority = EXCLUDED.queue_priority, " +
        // contact-history-opens-by-assignment (design D7): a queue owner names its queue; any other owner keeps the
        // stored one, so an assignment to an agent, a transfer to an agent or a close never clears it.
        "  queue_id = COALESCE(" + QueueIdWriteCase + ", conversations.queue_id)";

    public async Task SaveAsync(Conversation conversation, CancellationToken ct)
    {
        try
        {
            await _dataSource.ExecuteAsync(SaveSql, p => BindSave(p, conversation), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "uq_conversations_voice_linked_id")
        {
            // A concurrent / leadership-failover re-emission for the same physical call
            // (same tenant + voice_linked_id) — the voice Conversation is already tracked.
            // Idempotent no-op: this is the failover safety net behind the leader-emit gate
            // (see migration 027). Mirrors the InMemoryConversationStore uniqueness guard.
        }
    }

    /// <summary>
    /// Saves <paramref name="conversation"/> inside <paramref name="tx"/>: the licensed-agent writer commits an
    /// ownership change with its ledger row. An ownership change saves an existing conversation, so the
    /// voice-link insert race the standalone save absorbs cannot occur here.
    /// </summary>
    internal static Task SaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Conversation conversation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(conversation);
        return conn.ExecuteAsync(SaveSql, p => BindSave(p, conversation), tx, ct);
    }

    private static void BindSave(NpgsqlParameterCollection p, Conversation conversation)
    {
        var metadataJson = JsonSerializer.Serialize(
            conversation.Metadata,
            PostgresJson.Ctx.IReadOnlyDictionaryStringString);

        p.Add(new NpgsqlParameter("ConversationId", conversation.ConversationId.Value));
        p.Add(new NpgsqlParameter("TenantId", conversation.TenantId.Value));
        p.Add(new NpgsqlParameter("ContactId", conversation.ContactId.Value));
        p.Add(new NpgsqlParameter("Channel", (int)conversation.Channel));
        p.Add(new NpgsqlParameter("State", (int)conversation.State));
        var owner = conversation.Owner;
        p.Add(new NpgsqlParameter("OwnerKind", NpgsqlDbType.Integer) { Value = owner is null ? DBNull.Value : (int)owner.Kind });
        p.Add(new NpgsqlParameter("OwnerId", NpgsqlDbType.Text) { Value = owner?.OwnerId is { } ownerId ? ownerId.Value : DBNull.Value });
        p.Add(new NpgsqlParameter("CaseId", NpgsqlDbType.Text) { Value = conversation.CaseId is { } caseId ? caseId.Value : DBNull.Value });
        p.Add(new NpgsqlParameter("Metadata", metadataJson));
        p.Add(new NpgsqlParameter("CreatedAt", conversation.CreatedAt));
        p.Add(new NpgsqlParameter("ClosedAt", NpgsqlDbType.TimestampTz) { Value = (object?)conversation.ClosedAt ?? DBNull.Value });
        p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)conversation.UpdatedAt ?? DBNull.Value });
        p.Add(new NpgsqlParameter("CreatedBy", NpgsqlDbType.Text) { Value = (object?)conversation.CreatedBy ?? DBNull.Value });
        p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = (object?)conversation.UpdatedBy ?? DBNull.Value });
        p.Add(new NpgsqlParameter("VoiceLinkedId", NpgsqlDbType.Text) { Value = (object?)conversation.VoiceLinkedId ?? DBNull.Value });
        p.Add(new NpgsqlParameter("QueuePriority", conversation.QueuePriority));
        p.Add(new NpgsqlParameter("LastMessageAt", NpgsqlDbType.TimestampTz) { Value = (object?)conversation.LastMessageAt ?? DBNull.Value });
    }

    public async Task<bool> TryConditionalCloseWrapUpAsync(
        TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        // State-based compare-and-set (ADR-0034 Decision 6): close ONLY if still WrapUp. A concurrent
        // human typify that lands first transitions the row out of WrapUp (20), so the predicate
        // matches 0 rows and the autonomous caller skips stamping. Ordinals are bound as parameters
        // (no string interpolation of values); WrapUp = 20, Closed = 50 (ConversationState).
        var affected = await _dataSource.ExecuteAsync(
            "UPDATE conversations SET state = @ClosedState, updated_at = @Now, closed_at = @Now " +
            "WHERE conversation_id = @ConversationId AND tenant_id = @TenantId AND state = @WrapUpState",
            p =>
            {
                p.Add(new NpgsqlParameter("ConversationId", conversationId.Value));
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ClosedState", (int)ConversationState.Closed));
                p.Add(new NpgsqlParameter("WrapUpState", (int)ConversationState.WrapUp));
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            },
            ct);
        return affected == 1;
    }

    public async Task<bool> TryCommitWrapUpAsync(
        TenantId tenantId, EntityId conversationId, EntityId ownerAgentId, DateTimeOffset now, CancellationToken ct)
    {
        // State-and-owner compare-and-set (close-chat-without-typification-licence D3): only the caller whose
        // UPDATE matches the agent-held row of THIS owner wins, so concurrent wrap-ups and closes release the
        // owner's capacity unit once (see the interface remarks for the gap). owner_kind is an int and owner_id text, as BindSave writes them. @From is
        // FailoverWorkStates {Active, OnHold, Consulting}; the owner is kept.
        var from = ConversationStateMachine.FailoverWorkStates.Select(s => (int)s).ToArray();
        var affected = await _dataSource.ExecuteAsync(
            "UPDATE conversations SET state = @WrapUpState, updated_at = @Now " +
            "WHERE conversation_id = @ConversationId AND tenant_id = @TenantId " +
            "AND state = ANY(@From) AND owner_kind = @AgentKind AND owner_id = @Owner",
            p =>
            {
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = conversationId.Value });
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("WrapUpState", NpgsqlDbType.Integer) { Value = (int)ConversationState.WrapUp });
                p.Add(new NpgsqlParameter("From", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = from });
                p.Add(new NpgsqlParameter("AgentKind", NpgsqlDbType.Integer) { Value = (int)ConversationOwnerKind.Agent });
                p.Add(new NpgsqlParameter("Owner", NpgsqlDbType.Text) { Value = ownerAgentId.Value });
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            },
            ct);
        return affected == 1;
    }

    public async Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
    {
        // Terminal states: Closed = 3, Abandoned = 4 (check ConversationStateMachine)
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations " +
            "WHERE tenant_id = @TenantId AND contact_id = @ContactId AND channel = @Channel " +
            "  AND state NOT IN (SELECT unnest(@TerminalStates)) " +
            "ORDER BY created_at DESC LIMIT 1",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ContactId", contactId.Value));
                p.Add(new NpgsqlParameter("Channel", (int)channel));
                p.Add(new NpgsqlParameter("TerminalStates", GetTerminalStateInts()));
            },
            ConversationRow.Map, ct);
        return row?.ToConversation();
    }

    public async Task<DateTimeOffset?> FindLastInboundAtAsync(
        TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT m.created_at FROM messages m " +
            "JOIN conversations c ON c.tenant_id = m.tenant_id AND c.conversation_id = m.conversation_id " +
            "WHERE m.tenant_id = @TenantId AND c.contact_id = @ContactId AND c.channel = @Channel " +
            "  AND m.direction = @Inbound " +
            "ORDER BY m.created_at DESC LIMIT 1",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ContactId", contactId.Value));
                p.Add(new NpgsqlParameter("Channel", NpgsqlDbType.Integer) { Value = (int)channel });
                p.Add(new NpgsqlParameter("Inbound", NpgsqlDbType.Integer) { Value = (int)MessageDirection.Inbound });
            },
            r => r.GetDateTime("created_at"), ct);
        // timestamptz reads as a UTC DateTime; ToUniversalTime keeps the instant (gate #11).
        return rows.Count == 0 ? null : new DateTimeOffset(rows[0].ToUniversalTime());
    }

    public async Task<Conversation?> FindByVoiceLinkedIdAsync(TenantId tenantId, string voiceLinkedId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations WHERE tenant_id = @TenantId AND voice_linked_id = @VoiceLinkedId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("VoiceLinkedId", voiceLinkedId));
            },
            ConversationRow.Map, ct);
        return row?.ToConversation();
    }

    public async Task<Conversation?> FindByVoiceLinkedIdAcrossTenantsAsync(string voiceLinkedId, CancellationToken ct)
    {
        // voice_linked_id is the Asterisk call-global LinkedId — unique per call on a given
        // Asterisk — so QueryFirstOrDefault returns the one tracked conversation regardless of
        // tenant. Used only for failover tenant recovery on hangup (see IConversationStore).
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations WHERE voice_linked_id = @VoiceLinkedId ORDER BY created_at DESC",
            p => p.Add(new NpgsqlParameter("VoiceLinkedId", voiceLinkedId)),
            ConversationRow.Map, ct);
        return row?.ToConversation();
    }

    public async Task<Conversation?> FindByIdAcrossTenantsAsync(EntityId conversationId, CancellationToken ct)
    {
        // conversation_id is globally unique, so this resolves at most one row regardless of tenant.
        // Used only by the in-process voice-CSAT capture sink to recover the row's tenant partition
        // (the Pro CsatCapture carries no tenant — see IConversationStore).
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations WHERE conversation_id = @ConversationId",
            p => p.Add(new NpgsqlParameter("ConversationId", conversationId.Value)),
            ConversationRow.Map, ct);
        return row?.ToConversation();
    }

    public async Task<bool> HoldsOpenConversationOfContactAsync(TenantId tenantId, EntityId contactId, EntityId agentId, CancellationToken ct)
    {
        // contact-history-opens-by-assignment (design D2): owner kind and id together, any non-terminal state (the
        // terminal set bound from ConversationStateMachine). Served by idx_conversations_contact (tenant_id,
        // contact_id, state). An offered conversation is owned by its queue, so an offer is not holding.
        var holds = await _dataSource.ExecuteScalarAsync<bool?>(
            "SELECT EXISTS (SELECT 1 FROM conversations " +
            "WHERE tenant_id = @TenantId AND contact_id = @ContactId AND state <> ALL(@TerminalStates) " +
            "  AND owner_kind = @OwnerKind AND owner_id = @AgentId)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId.Value });
                p.Add(new NpgsqlParameter("TerminalStates", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = s_terminalStateValues });
                p.Add(new NpgsqlParameter("OwnerKind", NpgsqlDbType.Integer) { Value = (int)ConversationOwnerKind.Agent });
                p.Add(new NpgsqlParameter("AgentId", NpgsqlDbType.Text) { Value = agentId.Value });
            },
            ct);
        return holds ?? false;
    }

    public async Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations WHERE tenant_id = @TenantId AND contact_id = @ContactId " +
            "ORDER BY created_at DESC",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ContactId", contactId.Value));
            },
            ConversationRow.Map, ct);
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        return await _dataSource.ExecuteAsync(
            "DELETE FROM conversations WHERE tenant_id = @TenantId AND contact_id = @ContactId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ContactId", contactId.Value));
            },
            ct);
    }

    public async Task<bool> DeleteIfNoMessagesAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        // One statement: the emptiness check and the delete see the same snapshot.
        var deleted = await _dataSource.ExecuteAsync(
            "DELETE FROM conversations WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "AND NOT EXISTS (SELECT 1 FROM messages WHERE tenant_id = @TenantId AND conversation_id = @ConversationId)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", conversationId.Value));
            },
            ct);
        return deleted > 0;
    }

    public async Task<int> DeleteClosedBeforeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        // A terminal row is purged counted from its close. Every terminal write stamps closed_at; a row without one
        // compares as NULL and is kept rather than guessed at. Resolved also counts as terminal for the purge
        // (operator decision 2026-11-03); its clock is closed_at when set, else its last change (updated_at), else
        // created_at when updated_at is NULL — the clock the previous purge used, so this never deletes a row it would
        // have kept. Resolved rows carry closed_at NULL today (nothing closes them on its own). A Resolved row also
        // needs no message at or after the cutoff, because a customer reply is appended to messages without touching
        // the row (the probe is a range seek on idx_messages_conversation (tenant_id, conversation_id, created_at)).
        // Any other state is live and never purged.
        return await _dataSource.ExecuteAsync(
            "DELETE FROM conversations c WHERE c.tenant_id = @TenantId AND (" +
            "(c.state = ANY(@TerminalStates) AND c.closed_at < @Cutoff) OR " +
            "(c.state = @ResolvedState AND COALESCE(c.closed_at, c.updated_at, c.created_at) < @Cutoff " +
            "AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.tenant_id = c.tenant_id " +
            "AND m.conversation_id = c.conversation_id AND m.created_at >= @Cutoff)))",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("TerminalStates", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                {
                    Value = s_terminalStateValues,
                });
                p.Add(new NpgsqlParameter("ResolvedState", NpgsqlDbType.Integer) { Value = (int)ConversationState.Resolved });
                p.Add(new NpgsqlParameter("Cutoff", NpgsqlDbType.TimestampTz) { Value = cutoff });
            },
            ct);
    }

    public async Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            // Only a queue-owned conversation is routable by QueueDistributionWorker; letting
            // ownerless Queued rows into the bounded window would starve the routable ones.
            "FROM conversations WHERE tenant_id = @TenantId AND state = @State " +
            "AND owner_kind = @OwnerKind AND owner_id IS NOT NULL " +
            "ORDER BY queue_priority ASC, created_at ASC LIMIT @Limit",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("State", (object)(int)ConversationState.Queued));
                p.Add(new NpgsqlParameter("OwnerKind", NpgsqlDbType.Integer) { Value = (int)ConversationOwnerKind.Queue });
                p.Add(new NpgsqlParameter("Limit", limit));
            },
            ConversationRow.Map, ct);
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations WHERE tenant_id = @TenantId AND state = @State " +
            "ORDER BY queue_priority ASC, created_at ASC LIMIT @Limit",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("State", (int)state));
                p.Add(new NpgsqlParameter("Limit", limit));
            },
            ConversationRow.Map, ct);
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<int> CountActiveWorkAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        // W4 — owner-scoped + tenant-scoped count of engaged conversations
        // (ConversationStateMachine.ActiveWorkStates). Covers voice + digital; the
        // voice bridge models a live call as a Conversation owned by the agent.
        var activeWorkStates = ConversationStateMachine.ActiveWorkStates.Select(s => (int)s).ToArray();
        var count = await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM conversations " +
            "WHERE tenant_id = @TenantId AND owner_kind = @OwnerKind AND owner_id = @AgentId " +
            "  AND state = ANY(@States)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("OwnerKind", (int)ConversationOwnerKind.Agent));
                p.Add(new NpgsqlParameter("AgentId", agentId.Value));
                p.Add(new NpgsqlParameter("States", activeWorkStates));
            },
            ct);
        return (int)(count ?? 0L);
    }

    public async Task<IReadOnlyList<Conversation>> ListFailoverWorkByOwnerAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        // W5 — owner-scoped + tenant-scoped list of conversations with a LIVE customer
        // (ConversationStateMachine.FailoverWorkStates {Active,OnHold,Consulting}; excludes
        // WrapUp/parked/pre-accept). Returns full Conversation entities so the failover
        // sweep can re-queue each. int[] = ANY(@States) mirrors CountActiveWorkAsync.
        var failoverWorkStates = ConversationStateMachine.FailoverWorkStates.Select(s => (int)s).ToArray();
        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations " +
            "WHERE tenant_id = @TenantId AND owner_kind = @OwnerKind AND owner_id = @AgentId " +
            "  AND state = ANY(@States)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("OwnerKind", (int)ConversationOwnerKind.Agent));
                p.Add(new NpgsqlParameter("AgentId", agentId.Value));
                p.Add(new NpgsqlParameter("States", failoverWorkStates));
            },
            ConversationRow.Map, ct);
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<IReadOnlyList<Conversation>> ListPendingCallbackEvalAsync(CancellationToken ct)
    {
        // W5b — cross-tenant list of answered voice conversations parked in WrapUp whose metadata
        // carries the pendingCallbackEval marker (jsonb ->> accessor). The leader-gated
        // CallbackRescueWorker iterates these and groups by tenant; it clears the marker when it
        // enqueues a callback or escalates, so the set self-bounds.
        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations " +
            "WHERE state = @State AND metadata->>'pendingCallbackEval' = 'true'",
            p =>
            {
                p.Add(new NpgsqlParameter("State", (int)ConversationState.WrapUp));
            },
            ConversationRow.Map, ct);
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<IReadOnlyList<Conversation>> ListCallbackStuckAsync(TenantId tenantId, CancellationToken ct)
    {
        // W5b — tenant-scoped list of voice conversations parked in WrapUp whose rescue callbacks
        // exhausted the retry cap (metadata callbackStuck jsonb ->> accessor). Surfaced in the
        // supervisor stuck-work view so a human can re-arm the rescue or close the call.
        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
            "FROM conversations " +
            "WHERE tenant_id = @TenantId AND state = @State AND metadata->>'callbackStuck' = 'true'",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("State", (int)ConversationState.WrapUp));
            },
            ConversationRow.Map, ct);
        return rows.Select(r => r.ToConversation()).ToList();
    }

    // ── conversations-close-after-inactivity (design D3, D4, D7, D8, D9, D12) ─────────────────────────────

    // The sendable states of a claim (D9): FailoverWorkStates {Active, OnHold, Consulting}.
    private static readonly int[] s_sendableStateValues =
        ConversationStateMachine.FailoverWorkStates.Select(state => (int)state).ToArray();

    /// <summary>
    /// The sweep's list (D4). <c>state = ANY(@States) AND last_message_at &lt; @Cutoff</c> is a plain range on
    /// <c>idx_conversations_state_last_message (tenant_id, state, last_message_at)</c>, so the planner puts both in the
    /// Index Cond and never visits a row inside the window; <c>channel</c> filters the few rows past the cutoff. No
    /// fallback on the clock (the column is NOT NULL).
    /// </summary>
    internal const string ListInactiveSql =
        "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
        "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority, last_message_at, queue_id " +
        "FROM conversations " +
        "WHERE tenant_id = @TenantId AND state = ANY(@States) AND last_message_at < @Cutoff AND channel <> @Voice " +
        "ORDER BY last_message_at LIMIT @Limit";

    internal static void BindListInactive(
        NpgsqlParameterCollection p, TenantId tenantId, IReadOnlyCollection<ConversationState> states, DateTimeOffset cutoff, int limit)
    {
        p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
        p.Add(new NpgsqlParameter("States", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = states.Select(s => (int)s).ToArray() });
        p.Add(new NpgsqlParameter("Cutoff", NpgsqlDbType.TimestampTz) { Value = cutoff.UtcDateTime });
        p.Add(new NpgsqlParameter("Voice", NpgsqlDbType.Integer) { Value = (int)ChannelType.Voice });
        p.Add(new NpgsqlParameter("Limit", NpgsqlDbType.Integer) { Value = limit });
    }

    public async Task<IReadOnlyList<Conversation>> ListInactiveAsync(
        TenantId tenantId, IReadOnlyCollection<ConversationState> states, DateTimeOffset cutoff, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(states);
        var rows = await _dataSource.QueryListAsync(
            ListInactiveSql, p => BindListInactive(p, tenantId, states, cutoff, limit), ConversationRow.Map, ct);
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<InactivityCloseOutcome?> TryCloseInactiveAsync(TenantId tenantId, InactivityCloseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // D3: a read state outside the five is never closed here, and nothing is written.
        if (!ConversationStateMachine.CanCloseForInactivity(request.ReadState))
            return null;

        // D4: the ADR-0034 Decision 6 compare-and-set, applied to a second close. Under READ COMMITTED a claim or a
        // transfer that commits first changes the row and the re-evaluated predicate matches nothing (PLT-CNV-01).
        // D12: the reason is decided by the statement, from the clock it closes on, so every replica decides the same.
        // The owner is compared kind and id, null-safe (owner_kind int, owner_id text, as BindSave writes them).
        var rows = await _dataSource.QueryListAsync(
            "UPDATE conversations SET state = @ClosedState, closed_at = COALESCE(closed_at, @Now), updated_at = @Now, " +
            "  metadata = metadata || jsonb_build_object(@ReasonKey, " +
            "    CASE WHEN last_message_at < @BacklogCutoff THEN @BacklogReason ELSE @InactivityReason END) " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND state = @ReadState " +
            "  AND owner_kind IS NOT DISTINCT FROM @OwnerKind AND owner_id IS NOT DISTINCT FROM @OwnerId " +
            "  AND last_message_at < @Cutoff AND channel <> @Voice " +
            "RETURNING metadata ->> @ReasonKey AS reason, last_message_at",
            p =>
            {
                var owner = request.ReadOwner;
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = request.ConversationId.Value });
                p.Add(new NpgsqlParameter("ClosedState", NpgsqlDbType.Integer) { Value = (int)ConversationState.Closed });
                p.Add(new NpgsqlParameter("ReadState", NpgsqlDbType.Integer) { Value = (int)request.ReadState });
                p.Add(new NpgsqlParameter("OwnerKind", NpgsqlDbType.Integer) { Value = owner is null ? DBNull.Value : (int)owner.Kind });
                p.Add(new NpgsqlParameter("OwnerId", NpgsqlDbType.Text) { Value = owner?.OwnerId is { } ownerId ? ownerId.Value : DBNull.Value });
                p.Add(new NpgsqlParameter("Cutoff", NpgsqlDbType.TimestampTz) { Value = request.Cutoff.UtcDateTime });
                p.Add(new NpgsqlParameter("BacklogCutoff", NpgsqlDbType.TimestampTz) { Value = request.BacklogCutoff.UtcDateTime });
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = request.Now.UtcDateTime });
                p.Add(new NpgsqlParameter("Voice", NpgsqlDbType.Integer) { Value = (int)ChannelType.Voice });
                p.Add(new NpgsqlParameter("ReasonKey", NpgsqlDbType.Text) { Value = ConversationCloseReasons.MetadataKey });
                p.Add(new NpgsqlParameter("BacklogReason", NpgsqlDbType.Text) { Value = ConversationCloseReasons.InactivityBacklog });
                p.Add(new NpgsqlParameter("InactivityReason", NpgsqlDbType.Text) { Value = ConversationCloseReasons.Inactivity });
            },
            r => new InactivityCloseOutcome(
                r.GetString("reason"), new DateTimeOffset(r.GetDateTime("last_message_at").ToUniversalTime())),
            ct);
        return rows.Count == 1 ? rows[0] : null;
    }

    public async Task<bool> TryClaimForInboundAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        // D7: monotonic, on a row neither terminal nor Resolved (a Resolved row is D8's: a clock alone would not shield it
        // from the purge, which reads closed_at/updated_at — PLT-CNV-01 V1a). updated_at is left alone (D2).
        var affected = await _dataSource.ExecuteAsync(
            "UPDATE conversations SET last_message_at = GREATEST(COALESCE(last_message_at, '-infinity'::timestamptz), @Now) " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "AND state <> ALL(@TerminalStates) AND state <> @ResolvedState",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = conversationId.Value });
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
                p.Add(new NpgsqlParameter("TerminalStates", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = s_terminalStateValues });
                p.Add(new NpgsqlParameter("ResolvedState", NpgsqlDbType.Integer) { Value = (int)ConversationState.Resolved });
            },
            ct);
        return affected == 1;
    }

    public async Task<bool> TryCloseResolvedForReplyAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        // D8: Resolved → Closed is an existing edge; closed_at = now so the purge's clock keeps the row (PLT-CNV-01 P3).
        var affected = await _dataSource.ExecuteAsync(
            "UPDATE conversations SET state = @ClosedState, closed_at = @Now, updated_at = @Now, " +
            "  metadata = metadata || jsonb_build_object(@ReasonKey, @Reason) " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND state = @ResolvedState",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = conversationId.Value });
                p.Add(new NpgsqlParameter("ClosedState", NpgsqlDbType.Integer) { Value = (int)ConversationState.Closed });
                p.Add(new NpgsqlParameter("ResolvedState", NpgsqlDbType.Integer) { Value = (int)ConversationState.Resolved });
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
                p.Add(new NpgsqlParameter("ReasonKey", NpgsqlDbType.Text) { Value = ConversationCloseReasons.MetadataKey });
                p.Add(new NpgsqlParameter("Reason", NpgsqlDbType.Text) { Value = ConversationCloseReasons.CustomerReturned });
            },
            ct);
        return affected == 1;
    }

    public async Task<bool> TryClaimForSendAsync(
        TenantId tenantId, EntityId conversationId, DateTimeOffset now, bool advanceClock, CancellationToken ct)
    {
        // D9: only a sendable row matches; the clock moves only for a message that will be stored Pending.
        var affected = await _dataSource.ExecuteAsync(
            "UPDATE conversations SET last_message_at = CASE WHEN @Advance " +
            "  THEN GREATEST(COALESCE(last_message_at, '-infinity'::timestamptz), @Now) ELSE last_message_at END " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId AND state = ANY(@SendableStates)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = conversationId.Value });
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
                p.Add(new NpgsqlParameter("Advance", NpgsqlDbType.Boolean) { Value = advanceClock });
                p.Add(new NpgsqlParameter("SendableStates", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = s_sendableStateValues });
            },
            ct);
        return affected == 1;
    }

    /// <summary>
    /// D2's reconcile, one statement: for each of the tenant's non-terminal, non-voice rows, the newest activity message
    /// (<see cref="ActivityMessagePredicate"/>, computed per row the way migration 026 backfills, through
    /// <c>idx_messages_conversation</c>), applied only where it is newer than the clock. The outer WHERE re-checks the
    /// state and the clock on the row version it updates (READ COMMITTED re-evaluation), and <c>GREATEST</c> keeps the
    /// write monotonic, so concurrent reconciles, claims and closes never move a clock back or touch a closed row.
    /// </summary>
    internal const string ReconcileLastMessageAtSql =
        "WITH newest AS (" +
        "  SELECT o.conversation_id, " +
        "    (SELECT max(m.created_at) FROM messages m " +
        "     WHERE m.tenant_id = o.tenant_id AND m.conversation_id = o.conversation_id AND " + ActivityMessagePredicate + ") AS at " +
        "  FROM conversations o " +
        "  WHERE o.tenant_id = @TenantId AND o.state <> ALL(@TerminalStates) AND o.channel <> @Voice) " +
        "UPDATE conversations c SET last_message_at = GREATEST(c.last_message_at, newest.at) " +
        "FROM newest " +
        "WHERE c.tenant_id = @TenantId AND c.conversation_id = newest.conversation_id " +
        "  AND newest.at > c.last_message_at AND c.state <> ALL(@TerminalStates)";

    public Task<int> ReconcileLastMessageAtAsync(TenantId tenantId, CancellationToken ct) =>
        _dataSource.ExecuteAsync(
            ReconcileLastMessageAtSql,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("TerminalStates", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = s_terminalStateValues });
                p.Add(new NpgsqlParameter("Voice", NpgsqlDbType.Integer) { Value = (int)ChannelType.Voice });
            },
            ct);

    private static int[] GetTerminalStateInts()
    {
        var terminalStates = new List<int>();
        foreach (ConversationState state in Enum.GetValues<ConversationState>())
        {
            if (ConversationStateMachine.IsTerminal(state))
                terminalStates.Add((int)state);
        }
        return [.. terminalStates];
    }

    private sealed class ConversationRow
    {
        public string conversation_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string contact_id { get; init; } = null!;
        public int channel { get; init; }
        public int state { get; init; }
        public int? owner_kind { get; init; }
        public string? owner_id { get; init; }
        public string? case_id { get; init; }
        public string metadata { get; init; } = null!;
        public DateTime created_at { get; init; }
        public DateTime? closed_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }
        public string? voice_linked_id { get; init; }
        public int queue_priority { get; init; }
        public DateTime? last_message_at { get; init; }
        public string? queue_id { get; init; }

        public static ConversationRow Map(NpgsqlDataReader r) => new()
        {
            conversation_id = r.GetString("conversation_id"),
            tenant_id = r.GetString("tenant_id"),
            contact_id = r.GetString("contact_id"),
            channel = r.GetInt32("channel"),
            state = r.GetInt32("state"),
            owner_kind = r.GetInt32OrNull("owner_kind"),
            owner_id = r.GetStringOrNull("owner_id"),
            case_id = r.GetStringOrNull("case_id"),
            metadata = r.GetString("metadata"),
            created_at = r.GetDateTime("created_at"),
            closed_at = r.GetDateTimeOrNull("closed_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
            voice_linked_id = r.GetStringOrNull("voice_linked_id"),
            queue_priority = r.GetInt32("queue_priority"),
            last_message_at = r.GetDateTimeOrNull("last_message_at"),
            queue_id = r.GetStringOrNull("queue_id"),
        };

        public Conversation ToConversation()
        {
            ConversationOwner? owner = null;
            if (owner_kind.HasValue)
            {
                var kind = (ConversationOwnerKind)owner_kind.Value;
                var ownerId = owner_id != null ? EntityId.From(owner_id) : (EntityId?)null;
                owner = new ConversationOwner(kind, ownerId);
            }

            var metaDict = JsonSerializer.Deserialize(metadata, PostgresJson.Ctx.DictionaryStringString)
                           ?? new Dictionary<string, string>();

            var conv = new Conversation
            {
                ConversationId = EntityId.From(conversation_id),
                TenantId = new TenantId(tenant_id),
                ContactId = EntityId.From(contact_id),
                Channel = (ChannelType)channel,
                State = (ConversationState)state,
                Owner = owner,
                CaseId = case_id != null ? EntityId.From(case_id) : null,
                CreatedAt = created_at,
                ClosedAt = closed_at,
                UpdatedAt = updated_at,
                CreatedBy = created_by,
                UpdatedBy = updated_by,
                VoiceLinkedId = voice_linked_id,
                QueuePriority = queue_priority,
                LastMessageAt = last_message_at,
                QueueId = queue_id != null ? EntityId.From(queue_id) : null,
            };

            foreach (var kv in metaDict)
                conv.SetMetadata(kv.Key, kv.Value);

            return conv;
        }
    }
}
