using Dapper;
using Npgsql;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresWrapUpStore : IWrapUpStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWrapUpStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(WrapUpRecord record, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO wrap_up_records (tenant_id, conversation_id, agent_id, disposition_id, notes, duration_seconds, completed_at) " +
            "VALUES (@TenantId, @ConversationId, @AgentId, @DispositionId, @Notes, @DurationSeconds, @CompletedAt) " +
            "ON CONFLICT (tenant_id, conversation_id) DO UPDATE SET " +
            "  agent_id = EXCLUDED.agent_id, disposition_id = EXCLUDED.disposition_id, " +
            "  notes = EXCLUDED.notes, duration_seconds = EXCLUDED.duration_seconds, completed_at = EXCLUDED.completed_at",
            new
            {
                TenantId = record.TenantId.Value,
                ConversationId = record.ConversationId.Value,
                AgentId = record.AgentId.Value,
                DispositionId = record.DispositionId.Value,
                record.Notes,
                DurationSeconds = (long)record.Duration.TotalSeconds,
                record.CompletedAt,
            });
    }

    public async Task<WrapUpRecord?> GetByConversationIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<WrapUpRow>(
            "SELECT tenant_id, conversation_id, agent_id, disposition_id, notes, duration_seconds, completed_at " +
            "FROM wrap_up_records WHERE tenant_id = @TenantId AND conversation_id = @ConversationId",
            new { TenantId = tenantId.Value, ConversationId = conversationId.Value });
        return row?.ToRecord();
    }

    private sealed class WrapUpRow
    {
        public string tenant_id { get; init; } = null!;
        public string conversation_id { get; init; } = null!;
        public string agent_id { get; init; } = null!;
        public string disposition_id { get; init; } = null!;
        public string? notes { get; init; }
        public long duration_seconds { get; init; }
        public DateTime completed_at { get; init; }

        public WrapUpRecord ToRecord() => new()
        {
            TenantId = new TenantId(tenant_id),
            ConversationId = EntityId.From(conversation_id),
            AgentId = EntityId.From(agent_id),
            DispositionId = EntityId.From(disposition_id),
            Notes = notes,
            Duration = TimeSpan.FromSeconds(duration_seconds),
            CompletedAt = completed_at,
        };
    }
}
