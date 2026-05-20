using Npgsql;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantAddOnStore : ITenantAddOnStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantAddOnStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<TenantAddOn>> GetAsync(string tenantId, CancellationToken ct = default)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT tenant_id, feature, enabled_at FROM tenant_add_ons WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            TenantAddOnRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task UpsertAsync(TenantAddOn addOn, CancellationToken ct = default)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO tenant_add_ons (tenant_id, feature, enabled_at) " +
            "VALUES (@TenantId, @Feature, @EnabledAt) " +
            "ON CONFLICT (tenant_id, feature) DO UPDATE SET enabled_at = EXCLUDED.enabled_at",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", addOn.TenantId));
                p.Add(new NpgsqlParameter("Feature", addOn.Feature.ToString()));
                p.Add(new NpgsqlParameter("EnabledAt", addOn.EnabledAt.UtcDateTime));
            },
            ct);
    }

    public async Task DeleteAsync(string tenantId, PlanFeature feature, CancellationToken ct = default)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM tenant_add_ons WHERE tenant_id = @TenantId AND feature = @Feature",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId)); p.Add(new NpgsqlParameter("Feature", feature.ToString())); },
            ct);
    }

    private sealed class TenantAddOnRow
    {
        public string tenant_id { get; init; } = null!;
        public string feature { get; init; } = null!;
        public DateTime enabled_at { get; init; }

        public static TenantAddOnRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            feature = r.GetString("feature"),
            enabled_at = r.GetDateTime("enabled_at"),
        };

        public TenantAddOn ToModel() => new()
        {
            TenantId = tenant_id,
            Feature = Enum.Parse<PlanFeature>(feature),
            EnabledAt = new DateTimeOffset(enabled_at, TimeSpan.Zero),
        };
    }
}
