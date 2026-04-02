using Dapper;
using Npgsql;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantQuotaStore : ITenantQuotaStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantQuotaStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantQuota?> GetAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<QuotaRow?>(
            "SELECT tenant_id, max_concurrent_channels, max_active_campaigns, " +
            "max_monthly_voice_minutes, max_monthly_messages, max_storage_bytes, max_active_agents, quota_action " +
            "FROM tenant_quotas WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });

        return row?.ToQuota();
    }

    public async Task UpsertAsync(TenantQuota quota, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO tenant_quotas (tenant_id, max_concurrent_channels, max_active_campaigns, " +
            "max_monthly_voice_minutes, max_monthly_messages, max_storage_bytes, max_active_agents, quota_action) " +
            "VALUES (@TenantId, @MaxConcurrentChannels, @MaxActiveCampaigns, " +
            "@MaxMonthlyVoiceMinutes, @MaxMonthlyMessages, @MaxStorageBytes, @MaxActiveAgents, @QuotaAction) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "max_concurrent_channels = EXCLUDED.max_concurrent_channels, " +
            "max_active_campaigns = EXCLUDED.max_active_campaigns, " +
            "max_monthly_voice_minutes = EXCLUDED.max_monthly_voice_minutes, " +
            "max_monthly_messages = EXCLUDED.max_monthly_messages, " +
            "max_storage_bytes = EXCLUDED.max_storage_bytes, " +
            "max_active_agents = EXCLUDED.max_active_agents, " +
            "quota_action = EXCLUDED.quota_action",
            new
            {
                TenantId = quota.TenantId.Value,
                quota.MaxConcurrentChannels,
                quota.MaxActiveCampaigns,
                quota.MaxMonthlyVoiceMinutes,
                quota.MaxMonthlyMessages,
                quota.MaxStorageBytes,
                quota.MaxActiveAgents,
                QuotaAction = (short)quota.QuotaAction,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM tenant_quotas WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });
    }

    private sealed class QuotaRow
    {
        public string tenant_id { get; init; } = null!;
        public int max_concurrent_channels { get; init; }
        public int max_active_campaigns { get; init; }
        public long? max_monthly_voice_minutes { get; init; }
        public long? max_monthly_messages { get; init; }
        public long? max_storage_bytes { get; init; }
        public int? max_active_agents { get; init; }
        public short quota_action { get; init; }

        public TenantQuota ToQuota() => new()
        {
            TenantId = new TenantId(tenant_id),
            MaxConcurrentChannels = max_concurrent_channels,
            MaxActiveCampaigns = max_active_campaigns,
            MaxMonthlyVoiceMinutes = max_monthly_voice_minutes,
            MaxMonthlyMessages = max_monthly_messages,
            MaxStorageBytes = max_storage_bytes,
            MaxActiveAgents = max_active_agents,
            QuotaAction = (QuotaAction)quota_action,
        };
    }
}
