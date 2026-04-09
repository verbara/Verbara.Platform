using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresConversationStore : IConversationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresConversationStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ConversationRow>(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by " +
            "FROM conversations WHERE tenant_id = @TenantId AND conversation_id = @ConversationId",
            new { TenantId = tenantId.Value, ConversationId = conversationId.Value });
        return row?.ToConversation();
    }

    public async Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var whereClauses = new List<string> { "tenant_id = @TenantId" };
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId.Value);

        if (query.State.HasValue)
        {
            whereClauses.Add("state = @State");
            parameters.Add("State", (int)query.State.Value);
        }
        if (query.ContactId.HasValue)
        {
            whereClauses.Add("contact_id = @ContactId");
            parameters.Add("ContactId", query.ContactId.Value.Value);
        }
        if (query.CaseId.HasValue)
        {
            whereClauses.Add("case_id = @CaseId");
            parameters.Add("CaseId", query.CaseId.Value.Value);
        }
        if (query.AssignedAgentId.HasValue)
        {
            whereClauses.Add("owner_id = @OwnerId");
            parameters.Add("OwnerId", query.AssignedAgentId.Value.Value);
        }
        if (query.Channel.HasValue)
        {
            whereClauses.Add("channel = @Channel");
            parameters.Add("Channel", (int)query.Channel.Value);
        }

        var where = string.Join(" AND ", whereClauses);
        var offset = (query.Page - 1) * query.PageSize;

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM conversations WHERE {where}", parameters);

        parameters.Add("Limit", query.PageSize);
        parameters.Add("Offset", offset);

        var rows = await conn.QueryAsync<ConversationRow>(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            $"metadata, created_at, closed_at, updated_at, created_by, updated_by " +
            $"FROM conversations WHERE {where} ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            parameters);

        var items = rows.Select(r => r.ToConversation()).ToList();
        return new PagedResult<Conversation>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Conversation conversation, CancellationToken ct)
    {
        var metadataJson = JsonSerializer.Serialize(
            (Dictionary<string, string>)conversation.Metadata,
            PostgresJson.Ctx.DictionaryStringString);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by) " +
            "VALUES (@ConversationId, @TenantId, @ContactId, @Channel, @State, @OwnerKind, @OwnerId, @CaseId, " +
            "@Metadata::jsonb, @CreatedAt, @ClosedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, conversation_id) DO UPDATE SET " +
            "  state = EXCLUDED.state, owner_kind = EXCLUDED.owner_kind, owner_id = EXCLUDED.owner_id, " +
            "  case_id = EXCLUDED.case_id, metadata = EXCLUDED.metadata, closed_at = EXCLUDED.closed_at, " +
            "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            new
            {
                ConversationId = conversation.ConversationId.Value,
                TenantId = conversation.TenantId.Value,
                ContactId = conversation.ContactId.Value,
                Channel = (int)conversation.Channel,
                State = (int)conversation.State,
                OwnerKind = conversation.Owner != null ? (int?)conversation.Owner.Kind : null,
                OwnerId = conversation.Owner?.OwnerId?.Value,
                CaseId = conversation.CaseId?.Value,
                Metadata = metadataJson,
                conversation.CreatedAt,
                conversation.ClosedAt,
                conversation.UpdatedAt,
                conversation.CreatedBy,
                conversation.UpdatedBy,
            });
    }

    public async Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
    {
        // Terminal states: Closed = 3, Abandoned = 4 (check ConversationStateMachine)
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ConversationRow>(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by " +
            "FROM conversations " +
            "WHERE tenant_id = @TenantId AND contact_id = @ContactId AND channel = @Channel " +
            "  AND state NOT IN (SELECT unnest(@TerminalStates)) " +
            "ORDER BY created_at DESC LIMIT 1",
            new
            {
                TenantId = tenantId.Value,
                ContactId = contactId.Value,
                Channel = (int)channel,
                TerminalStates = GetTerminalStateInts(),
            });
        return row?.ToConversation();
    }

    public async Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ConversationRow>(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by " +
            "FROM conversations WHERE tenant_id = @TenantId AND contact_id = @ContactId " +
            "ORDER BY created_at DESC",
            new { TenantId = tenantId.Value, ContactId = contactId.Value });
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM conversations WHERE tenant_id = @TenantId AND contact_id = @ContactId",
            new { TenantId = tenantId.Value, ContactId = contactId.Value });
    }

    public async Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM conversations WHERE tenant_id = @TenantId AND created_at < @Cutoff",
            new { TenantId = tenantId.Value, Cutoff = cutoff });
    }

    public async Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ConversationRow>(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by " +
            "FROM conversations WHERE tenant_id = @TenantId AND state = @State " +
            "ORDER BY created_at ASC LIMIT @Limit",
            new { TenantId = tenantId.Value, State = (int)ConversationState.Queued, Limit = limit });
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ConversationRow>(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by " +
            "FROM conversations WHERE tenant_id = @TenantId AND state = @State " +
            "ORDER BY created_at ASC LIMIT @Limit",
            new { TenantId = tenantId.Value, State = (int)state, Limit = limit });
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
            };

            foreach (var kv in metaDict)
                conv.SetMetadata(kv.Key, kv.Value);

            return conv;
        }
    }
}
