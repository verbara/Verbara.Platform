using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantQuotaStore : ITenantQuotaStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantQuotaStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantQuota?> GetAsync(TenantId tenantId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT tenant_id, max_concurrent_channels, max_active_campaigns, " +
            "max_monthly_voice_minutes, max_monthly_messages, max_storage_bytes, max_active_agents, quota_action, ai_credits_monthly " +
            "FROM tenant_quotas WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            QuotaRow.Map, ct);

        return row?.ToQuota();
    }

    public async Task UpsertAsync(TenantQuota quota, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO tenant_quotas (tenant_id, max_concurrent_channels, max_active_campaigns, " +
            "max_monthly_voice_minutes, max_monthly_messages, max_storage_bytes, max_active_agents, quota_action, ai_credits_monthly) " +
            "VALUES (@TenantId, @MaxConcurrentChannels, @MaxActiveCampaigns, " +
            "@MaxMonthlyVoiceMinutes, @MaxMonthlyMessages, @MaxStorageBytes, @MaxActiveAgents, @QuotaAction, @AiCreditsMonthly) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "max_concurrent_channels = EXCLUDED.max_concurrent_channels, " +
            "max_active_campaigns = EXCLUDED.max_active_campaigns, " +
            "max_monthly_voice_minutes = EXCLUDED.max_monthly_voice_minutes, " +
            "max_monthly_messages = EXCLUDED.max_monthly_messages, " +
            "max_storage_bytes = EXCLUDED.max_storage_bytes, " +
            "max_active_agents = EXCLUDED.max_active_agents, " +
            "quota_action = EXCLUDED.quota_action, " +
            "ai_credits_monthly = EXCLUDED.ai_credits_monthly",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", quota.TenantId.Value));
                p.Add(new NpgsqlParameter("MaxConcurrentChannels", quota.MaxConcurrentChannels));
                p.Add(new NpgsqlParameter("MaxActiveCampaigns", quota.MaxActiveCampaigns));
                p.Add(new NpgsqlParameter("MaxMonthlyVoiceMinutes", NpgsqlDbType.Bigint) { Value = (object?)quota.MaxMonthlyVoiceMinutes ?? DBNull.Value });
                p.Add(new NpgsqlParameter("MaxMonthlyMessages", NpgsqlDbType.Bigint) { Value = (object?)quota.MaxMonthlyMessages ?? DBNull.Value });
                p.Add(new NpgsqlParameter("MaxStorageBytes", NpgsqlDbType.Bigint) { Value = (object?)quota.MaxStorageBytes ?? DBNull.Value });
                p.Add(new NpgsqlParameter("MaxActiveAgents", NpgsqlDbType.Integer) { Value = (object?)quota.MaxActiveAgents ?? DBNull.Value });
                p.Add(new NpgsqlParameter("QuotaAction", (short)quota.QuotaAction));
                p.Add(new NpgsqlParameter("AiCreditsMonthly", NpgsqlDbType.Bigint)
                    { Value = (object?)quota.AiCreditsMonthly ?? DBNull.Value });
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM tenant_quotas WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            ct);
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
        public long? ai_credits_monthly { get; init; }

        public static QuotaRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            max_concurrent_channels = r.GetInt32("max_concurrent_channels"),
            max_active_campaigns = r.GetInt32("max_active_campaigns"),
            max_monthly_voice_minutes = r.GetInt64OrNull("max_monthly_voice_minutes"),
            max_monthly_messages = r.GetInt64OrNull("max_monthly_messages"),
            max_storage_bytes = r.GetInt64OrNull("max_storage_bytes"),
            max_active_agents = r.GetInt32OrNull("max_active_agents"),
            quota_action = r.GetInt16("quota_action"),
            ai_credits_monthly = r.GetInt64OrNull("ai_credits_monthly"),
        };

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
            AiCreditsMonthly = ai_credits_monthly,
        };
    }
}
