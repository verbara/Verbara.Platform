using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Conversations;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresConversationStore : IConversationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresConversationStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
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
            whereClauses.Add("owner_id = @OwnerId");
            binders.Add(p => p.Add(new NpgsqlParameter("OwnerId", query.AssignedAgentId.Value.Value)));
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
            $"metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
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

    public async Task SaveAsync(Conversation conversation, CancellationToken ct)
    {
        var metadataJson = JsonSerializer.Serialize(
            (Dictionary<string, string>)conversation.Metadata,
            PostgresJson.Ctx.DictionaryStringString);

        try
        {
            await _dataSource.ExecuteAsync(
                "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
                "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority) " +
                "VALUES (@ConversationId, @TenantId, @ContactId, @Channel, @State, @OwnerKind, @OwnerId, @CaseId, " +
                "@Metadata::jsonb, @CreatedAt, @ClosedAt, @UpdatedAt, @CreatedBy, @UpdatedBy, @VoiceLinkedId, @QueuePriority) " +
                "ON CONFLICT (tenant_id, conversation_id) DO UPDATE SET " +
                "  state = EXCLUDED.state, owner_kind = EXCLUDED.owner_kind, owner_id = EXCLUDED.owner_id, " +
                "  case_id = EXCLUDED.case_id, metadata = EXCLUDED.metadata, closed_at = EXCLUDED.closed_at, " +
                "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by, " +
                // 3B.2d: an outbound voice Conversation is pre-created with a NULL voice_linked_id; the
                // bridge stamps the real LinkedId later, so it must persist on UPDATE (not just INSERT).
                "  voice_linked_id = EXCLUDED.voice_linked_id, " +
                // W5: a failover re-queue (priority -1) or a normal transfer (priority 0) must persist on UPDATE.
                "  queue_priority = EXCLUDED.queue_priority",
                p =>
                {
                    p.Add(new NpgsqlParameter("ConversationId", conversation.ConversationId.Value));
                    p.Add(new NpgsqlParameter("TenantId", conversation.TenantId.Value));
                    p.Add(new NpgsqlParameter("ContactId", conversation.ContactId.Value));
                    p.Add(new NpgsqlParameter("Channel", (int)conversation.Channel));
                    p.Add(new NpgsqlParameter("State", (int)conversation.State));
                    p.Add(new NpgsqlParameter("OwnerKind", NpgsqlDbType.Integer) { Value = (object?)(conversation.Owner != null ? (int?)conversation.Owner.Kind : null) ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("OwnerId", NpgsqlDbType.Text) { Value = (object?)conversation.Owner?.OwnerId?.Value ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("CaseId", NpgsqlDbType.Text) { Value = (object?)conversation.CaseId?.Value ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("Metadata", metadataJson));
                    p.Add(new NpgsqlParameter("CreatedAt", conversation.CreatedAt));
                    p.Add(new NpgsqlParameter("ClosedAt", NpgsqlDbType.TimestampTz) { Value = (object?)conversation.ClosedAt ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)conversation.UpdatedAt ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("CreatedBy", NpgsqlDbType.Text) { Value = (object?)conversation.CreatedBy ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = (object?)conversation.UpdatedBy ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("VoiceLinkedId", NpgsqlDbType.Text) { Value = (object?)conversation.VoiceLinkedId ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("QueuePriority", conversation.QueuePriority));
                },
                ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "uq_conversations_voice_linked_id")
        {
            // A concurrent / leadership-failover re-emission for the same physical call
            // (same tenant + voice_linked_id) — the voice Conversation is already tracked.
            // Idempotent no-op: this is the failover safety net behind the leader-emit gate
            // (see migration 027). Mirrors the InMemoryConversationStore uniqueness guard.
        }
    }

    public async Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
    {
        // Terminal states: Closed = 3, Abandoned = 4 (check ConversationStateMachine)
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
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

    public async Task<Conversation?> FindByVoiceLinkedIdAsync(TenantId tenantId, string voiceLinkedId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
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
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
            "FROM conversations WHERE voice_linked_id = @VoiceLinkedId ORDER BY created_at DESC",
            p => p.Add(new NpgsqlParameter("VoiceLinkedId", voiceLinkedId)),
            ConversationRow.Map, ct);
        return row?.ToConversation();
    }

    public async Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
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

    public async Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        return await _dataSource.ExecuteAsync(
            "DELETE FROM conversations WHERE tenant_id = @TenantId AND created_at < @Cutoff",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Cutoff", cutoff));
            },
            ct);
    }

    public async Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
            "FROM conversations WHERE tenant_id = @TenantId AND state = @State " +
            "ORDER BY queue_priority ASC, created_at ASC LIMIT @Limit",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("State", (object)(int)ConversationState.Queued));
                p.Add(new NpgsqlParameter("Limit", limit));
            },
            ConversationRow.Map, ct);
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
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
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
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
            "metadata, created_at, closed_at, updated_at, created_by, updated_by, voice_linked_id, queue_priority " +
            "FROM conversations " +
            "WHERE state = @State AND metadata->>'pendingCallbackEval' = 'true'",
            p =>
            {
                p.Add(new NpgsqlParameter("State", (int)ConversationState.WrapUp));
            },
            ConversationRow.Map, ct);
        return rows.Select(r => r.ToConversation()).ToList();
    }

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
            };

            foreach (var kv in metaDict)
                conv.SetMetadata(kv.Key, kv.Value);

            return conv;
        }
    }
}
