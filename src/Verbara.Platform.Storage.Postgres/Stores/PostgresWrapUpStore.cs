using Npgsql;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresWrapUpStore : IWrapUpStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWrapUpStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(WrapUpRecord record, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO wrap_up_records (tenant_id, conversation_id, agent_id, disposition_id, notes, duration_seconds, completed_at) " +
            "VALUES (@TenantId, @ConversationId, @AgentId, @DispositionId, @Notes, @DurationSeconds, @CompletedAt) " +
            "ON CONFLICT (tenant_id, conversation_id) DO UPDATE SET " +
            "  agent_id = EXCLUDED.agent_id, disposition_id = EXCLUDED.disposition_id, " +
            "  notes = EXCLUDED.notes, duration_seconds = EXCLUDED.duration_seconds, completed_at = EXCLUDED.completed_at",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", record.TenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", record.ConversationId.Value));
                p.Add(new NpgsqlParameter("AgentId", record.AgentId.Value));
                p.Add(new NpgsqlParameter("DispositionId", record.DispositionId.Value));
                p.Add(new NpgsqlParameter("Notes", (object?)record.Notes ?? DBNull.Value));
                p.Add(new NpgsqlParameter("DurationSeconds", (long)record.Duration.TotalSeconds));
                p.Add(new NpgsqlParameter("CompletedAt", record.CompletedAt.UtcDateTime));
            },
            ct);
    }

    public async Task<WrapUpRecord?> GetByConversationIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT tenant_id, conversation_id, agent_id, disposition_id, notes, duration_seconds, completed_at " +
            "FROM wrap_up_records WHERE tenant_id = @TenantId AND conversation_id = @ConversationId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", conversationId.Value));
            },
            WrapUpRow.Map, ct);
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

        public static WrapUpRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            conversation_id = r.GetString("conversation_id"),
            agent_id = r.GetString("agent_id"),
            disposition_id = r.GetString("disposition_id"),
            notes = r.GetStringOrNull("notes"),
            duration_seconds = r.GetInt64("duration_seconds"),
            completed_at = r.GetDateTime("completed_at"),
        };

        public WrapUpRecord ToRecord() => new()
        {
            TenantId = new TenantId(tenant_id),
            ConversationId = EntityId.From(conversation_id),
            AgentId = EntityId.From(agent_id),
            DispositionId = EntityId.From(disposition_id),
            Notes = notes,
            Duration = TimeSpan.FromSeconds(duration_seconds),
            CompletedAt = new DateTimeOffset(completed_at, TimeSpan.Zero),
        };
    }
}
