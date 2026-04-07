using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantStore : ITenantStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "tenant_id, name, status, type, parent_tenant_id, options, metadata, created_at, updated_at";

    public async ValueTask<Tenant?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<TenantRow>(
            $"SELECT {SelectColumns} FROM tenants WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
        return row?.ToTenant();
    }

    public async ValueTask<IReadOnlyList<Tenant>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<TenantRow>(
            $"SELECT {SelectColumns} FROM tenants WHERE status = @Status",
            new { Status = (int)TenantStatus.Active });
        return rows.Select(r => r.ToTenant()).ToList();
    }

    public async ValueTask UpsertAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Enforce: only one Platform tenant allowed
        if (tenant.Type == TenantType.Platform)
        {
            var existing = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT tenant_id FROM tenants WHERE type = @Type LIMIT 1",
                new { Type = (int)TenantType.Platform });
            if (existing is not null && existing != tenant.TenantId)
                throw new InvalidOperationException("Only one Platform tenant is allowed.");
        }

        // Enforce: max depth 3 (Platform -> Partner -> Customer)
        if (tenant.ParentTenantId is not null)
        {
            var parentParentId = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT parent_tenant_id FROM tenants WHERE tenant_id = @TenantId",
                new { TenantId = tenant.ParentTenantId });
            if (parentParentId is not null)
            {
                var grandparentParentId = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT parent_tenant_id FROM tenants WHERE tenant_id = @TenantId",
                    new { TenantId = parentParentId });
                if (grandparentParentId is not null)
                    throw new InvalidOperationException("Maximum tenant hierarchy depth (3 levels) exceeded.");
            }
        }

        var optionsJson = JsonSerializer.Serialize(tenant.Options, PostgresJson.Ctx.TenantOptions);
        var metadataJson = tenant.Metadata is not null
            ? JsonSerializer.Serialize(tenant.Metadata, PostgresJson.Ctx.DictionaryStringString)
            : null;

        await conn.ExecuteAsync(
            "INSERT INTO tenants (tenant_id, name, status, type, parent_tenant_id, options, metadata, created_at, updated_at) " +
            "VALUES (@TenantId, @Name, @Status, @Type, @ParentTenantId, @Options::jsonb, @Metadata::jsonb, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, status = EXCLUDED.status, options = EXCLUDED.options, " +
            "  metadata = EXCLUDED.metadata, updated_at = EXCLUDED.updated_at",
            new
            {
                tenant.TenantId,
                tenant.Name,
                Status = (int)tenant.Status,
                Type = (int)tenant.Type,
                tenant.ParentTenantId,
                Options = optionsJson,
                Metadata = metadataJson,
                CreatedAt = tenant.CreatedAt.UtcDateTime,
                UpdatedAt = tenant.UpdatedAt.UtcDateTime,
            });
    }

    public async ValueTask UpdateStatusAsync(string tenantId, TenantStatus status, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Block deletion if active children exist
        if (status == TenantStatus.Deleted)
        {
            var hasActiveChildren = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tenants WHERE parent_tenant_id = @TenantId AND status = @ActiveStatus)",
                new { TenantId = tenantId, ActiveStatus = (int)TenantStatus.Active });
            if (hasActiveChildren)
                throw new InvalidOperationException("Cannot delete tenant with active children.");
        }

        await conn.ExecuteAsync(
            "UPDATE tenants SET status = @Status, updated_at = @UpdatedAt WHERE tenant_id = @TenantId",
            new { TenantId = tenantId, Status = (int)status, UpdatedAt = DateTime.UtcNow });
    }

    public async ValueTask<Tenant?> GetHostTenantAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<TenantRow>(
            $"SELECT {SelectColumns} FROM tenants WHERE type = @Type",
            new { Type = (int)TenantType.Platform });
        return row?.ToTenant();
    }

    public async ValueTask<IReadOnlyList<Tenant>> GetChildrenAsync(string parentTenantId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<TenantRow>(
            $"SELECT {SelectColumns} FROM tenants WHERE parent_tenant_id = @ParentTenantId",
            new { ParentTenantId = parentTenantId });
        return rows.Select(r => r.ToTenant()).ToList();
    }

    private sealed class TenantRow
    {
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public int status { get; init; }
        public int type { get; init; }
        public string? parent_tenant_id { get; init; }
        public string options { get; init; } = null!;
        public string? metadata { get; init; }
        public DateTime created_at { get; init; }
        public DateTime updated_at { get; init; }

        public Tenant ToTenant()
        {
            var opts = JsonSerializer.Deserialize(options, PostgresJson.Ctx.TenantOptions) ?? new TenantOptions();
            var meta = metadata is not null
                ? JsonSerializer.Deserialize(metadata, PostgresJson.Ctx.DictionaryStringString)
                : null;

            return new Tenant
            {
                TenantId = tenant_id,
                Name = name,
                Status = (TenantStatus)status,
                Type = (TenantType)type,
                ParentTenantId = parent_tenant_id,
                Options = opts,
                Metadata = meta,
                CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(updated_at, TimeSpan.Zero),
            };
        }
    }
}
