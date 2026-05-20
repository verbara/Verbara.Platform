using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAgentCapacityStore : IAgentCapacityStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAgentCapacityStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<AgentCapacityRecord>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT tenant_id, agent_id, voice_load, chat_load, email_load, sms_load, updated_at " +
            "FROM agent_capacity WHERE tenant_id = @TenantId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); },
            AgentCapacityRow.Map, ct);

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task UpsertAsync(AgentCapacityRecord record, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO agent_capacity (tenant_id, agent_id, voice_load, chat_load, email_load, sms_load, updated_at) " +
            "VALUES (@TenantId, @AgentId, @VoiceLoad, @ChatLoad, @EmailLoad, @SmsLoad, @UpdatedAt) " +
            "ON CONFLICT (tenant_id, agent_id) DO UPDATE SET " +
            "  voice_load = EXCLUDED.voice_load, " +
            "  chat_load  = EXCLUDED.chat_load, " +
            "  email_load = EXCLUDED.email_load, " +
            "  sms_load   = EXCLUDED.sms_load, " +
            "  updated_at = EXCLUDED.updated_at",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", record.TenantId));
                p.Add(new NpgsqlParameter("AgentId", record.AgentId));
                p.Add(new NpgsqlParameter("VoiceLoad", record.VoiceLoad));
                p.Add(new NpgsqlParameter("ChatLoad", record.ChatLoad));
                p.Add(new NpgsqlParameter("EmailLoad", record.EmailLoad));
                p.Add(new NpgsqlParameter("SmsLoad", record.SmsLoad));
                p.Add(new NpgsqlParameter("UpdatedAt", record.UpdatedAt.UtcDateTime));
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM agent_capacity WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("AgentId", agentId.Value)); },
            ct);
    }

    public async Task ClearAllAsync(CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM agent_capacity",
            p => { },
            ct);
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

        public static AgentCapacityRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            agent_id = r.GetString("agent_id"),
            voice_load = r.GetInt32("voice_load"),
            chat_load = r.GetInt32("chat_load"),
            email_load = r.GetInt32("email_load"),
            sms_load = r.GetInt32("sms_load"),
            updated_at = r.GetDateTime("updated_at"),
        };

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
