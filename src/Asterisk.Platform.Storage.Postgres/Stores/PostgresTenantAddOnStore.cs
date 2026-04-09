using Dapper;
using Npgsql;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantAddOnStore : ITenantAddOnStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantAddOnStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<TenantAddOn>> GetAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TenantAddOnRow>(
            "SELECT tenant_id, feature, enabled_at FROM tenant_add_ons WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task UpsertAsync(TenantAddOn addOn, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO tenant_add_ons (tenant_id, feature, enabled_at) " +
            "VALUES (@TenantId, @Feature, @EnabledAt) " +
            "ON CONFLICT (tenant_id, feature) DO UPDATE SET enabled_at = EXCLUDED.enabled_at",
            new
            {
                TenantId  = addOn.TenantId,
                Feature   = addOn.Feature.ToString(),
                EnabledAt = addOn.EnabledAt.UtcDateTime,
            });
    }

    public async Task DeleteAsync(string tenantId, PlanFeature feature, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM tenant_add_ons WHERE tenant_id = @TenantId AND feature = @Feature",
            new { TenantId = tenantId, Feature = feature.ToString() });
    }

    private sealed class TenantAddOnRow
    {
        public string tenant_id { get; init; } = null!;
        public string feature { get; init; } = null!;
        public DateTime enabled_at { get; init; }

        public TenantAddOn ToModel() => new()
        {
            TenantId  = tenant_id,
            Feature   = Enum.Parse<PlanFeature>(feature),
            EnabledAt = new DateTimeOffset(enabled_at, TimeSpan.Zero),
        };
    }
}
