using Dapper;
using Npgsql;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantRetentionPolicyStore : ITenantRetentionPolicyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantRetentionPolicyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RetentionRow>(
            "SELECT tenant_id, conversation_retention_days, auth_event_retention_days, " +
            "audit_retention_days, usage_record_retention_days " +
            "FROM tenant_retention_policies WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
        return row?.ToPolicy();
    }

    public async Task SaveAsync(TenantRetentionPolicy policy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO tenant_retention_policies (tenant_id, conversation_retention_days, auth_event_retention_days, " +
            "audit_retention_days, usage_record_retention_days) " +
            "VALUES (@TenantId, @ConversationRetentionDays, @AuthEventRetentionDays, @AuditRetentionDays, @UsageRecordRetentionDays) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  conversation_retention_days = EXCLUDED.conversation_retention_days, " +
            "  auth_event_retention_days = EXCLUDED.auth_event_retention_days, " +
            "  audit_retention_days = EXCLUDED.audit_retention_days, " +
            "  usage_record_retention_days = EXCLUDED.usage_record_retention_days",
            new
            {
                policy.TenantId,
                policy.ConversationRetentionDays,
                policy.AuthEventRetentionDays,
                policy.AuditRetentionDays,
                policy.UsageRecordRetentionDays,
            });
    }

    public async Task<IReadOnlyList<TenantRetentionPolicy>> ListActiveAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RetentionRow>(
            "SELECT tenant_id, conversation_retention_days, auth_event_retention_days, " +
            "audit_retention_days, usage_record_retention_days " +
            "FROM tenant_retention_policies " +
            "WHERE conversation_retention_days IS NOT NULL " +
            "   OR auth_event_retention_days IS NOT NULL " +
            "   OR audit_retention_days IS NOT NULL " +
            "   OR usage_record_retention_days IS NOT NULL");
        return rows.Select(r => r.ToPolicy()).ToList();
    }

    private sealed class RetentionRow
    {
        public string tenant_id { get; init; } = null!;
        public int? conversation_retention_days { get; init; }
        public int? auth_event_retention_days { get; init; }
        public int? audit_retention_days { get; init; }
        public int? usage_record_retention_days { get; init; }

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
