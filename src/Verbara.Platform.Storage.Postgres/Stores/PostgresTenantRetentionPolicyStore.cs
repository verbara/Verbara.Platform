using Npgsql;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantRetentionPolicyStore : ITenantRetentionPolicyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantRetentionPolicyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT tenant_id, conversation_retention_days, auth_event_retention_days, " +
            "audit_retention_days, usage_record_retention_days " +
            "FROM tenant_retention_policies WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            RetentionRow.Map, ct);
        return row?.ToPolicy();
    }

    public async Task SaveAsync(TenantRetentionPolicy policy, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO tenant_retention_policies (tenant_id, conversation_retention_days, auth_event_retention_days, " +
            "audit_retention_days, usage_record_retention_days) " +
            "VALUES (@TenantId, @ConversationRetentionDays, @AuthEventRetentionDays, @AuditRetentionDays, @UsageRecordRetentionDays) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  conversation_retention_days = EXCLUDED.conversation_retention_days, " +
            "  auth_event_retention_days = EXCLUDED.auth_event_retention_days, " +
            "  audit_retention_days = EXCLUDED.audit_retention_days, " +
            "  usage_record_retention_days = EXCLUDED.usage_record_retention_days",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", policy.TenantId));
                p.Add(new NpgsqlParameter("ConversationRetentionDays", (object?)policy.ConversationRetentionDays ?? DBNull.Value));
                p.Add(new NpgsqlParameter("AuthEventRetentionDays", (object?)policy.AuthEventRetentionDays ?? DBNull.Value));
                p.Add(new NpgsqlParameter("AuditRetentionDays", (object?)policy.AuditRetentionDays ?? DBNull.Value));
                p.Add(new NpgsqlParameter("UsageRecordRetentionDays", (object?)policy.UsageRecordRetentionDays ?? DBNull.Value));
            },
            ct);
    }

    public async Task<IReadOnlyList<TenantRetentionPolicy>> ListActiveAsync(CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT tenant_id, conversation_retention_days, auth_event_retention_days, " +
            "audit_retention_days, usage_record_retention_days " +
            "FROM tenant_retention_policies " +
            "WHERE conversation_retention_days IS NOT NULL " +
            "   OR auth_event_retention_days IS NOT NULL " +
            "   OR audit_retention_days IS NOT NULL " +
            "   OR usage_record_retention_days IS NOT NULL",
            p => { },
            RetentionRow.Map, ct);
        return rows.Select(r => r.ToPolicy()).ToList();
    }

    private sealed class RetentionRow
    {
        public string tenant_id { get; init; } = null!;
        public int? conversation_retention_days { get; init; }
        public int? auth_event_retention_days { get; init; }
        public int? audit_retention_days { get; init; }
        public int? usage_record_retention_days { get; init; }

        public static RetentionRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            conversation_retention_days = r.GetInt32OrNull("conversation_retention_days"),
            auth_event_retention_days = r.GetInt32OrNull("auth_event_retention_days"),
            audit_retention_days = r.GetInt32OrNull("audit_retention_days"),
            usage_record_retention_days = r.GetInt32OrNull("usage_record_retention_days"),
        };

        public TenantRetentionPolicy ToPolicy() => new()
        {
            TenantId = tenant_id,
            ConversationRetentionDays = conversation_retention_days,
            AuthEventRetentionDays = auth_event_retention_days,
            AuditRetentionDays = audit_retention_days,
            UsageRecordRetentionDays = usage_record_retention_days,
        };
    }
}
