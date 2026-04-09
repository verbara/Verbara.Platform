using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues.Services;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAgentCapacityStore : IAgentCapacityStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAgentCapacityStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<AgentCapacityRecord>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AgentCapacityRow>(
            "SELECT tenant_id, agent_id, voice_load, chat_load, email_load, sms_load, updated_at " +
            "FROM agent_capacity WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task UpsertAsync(AgentCapacityRecord record, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO agent_capacity (tenant_id, agent_id, voice_load, chat_load, email_load, sms_load, updated_at) " +
            "VALUES (@TenantId, @AgentId, @VoiceLoad, @ChatLoad, @EmailLoad, @SmsLoad, @UpdatedAt) " +
            "ON CONFLICT (tenant_id, agent_id) DO UPDATE SET " +
            "  voice_load = EXCLUDED.voice_load, " +
            "  chat_load  = EXCLUDED.chat_load, " +
            "  email_load = EXCLUDED.email_load, " +
            "  sms_load   = EXCLUDED.sms_load, " +
            "  updated_at = EXCLUDED.updated_at",
            new
            {
                TenantId  = record.TenantId,
                AgentId   = record.AgentId,
                VoiceLoad = record.VoiceLoad,
                ChatLoad  = record.ChatLoad,
                EmailLoad = record.EmailLoad,
                SmsLoad   = record.SmsLoad,
                UpdatedAt = record.UpdatedAt.UtcDateTime,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM agent_capacity WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            new { TenantId = tenantId.Value, AgentId = agentId.Value });
    }

    public async Task ClearAllAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM agent_capacity");
    }

    private sealed class AgentCapacityRow
    {
        public string tenant_id { get; init; } = null!;
        public string agent_id { get; init; } = null!;
        public int voice_load { get; init; }
        public int chat_load { get; init; }
        public int email_load { get; init; }
        public int sms_load { get; init; }
        public DateTime updated_at { get; init; }

        public AgentCapacityRecord ToModel() => new()
        {
            TenantId  = tenant_id,
            AgentId   = agent_id,
            VoiceLoad = voice_load,
            ChatLoad  = chat_load,
            EmailLoad = email_load,
            SmsLoad   = sms_load,
            UpdatedAt = new DateTimeOffset(updated_at, TimeSpan.Zero),
        };
    }
}
