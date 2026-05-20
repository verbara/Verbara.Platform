using System.Text.Json;
using Npgsql;
using Verbara.Sdk.Data.Npgsql;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantStore : ITenantStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "tenant_id, name, status, type, parent_tenant_id, options, metadata, created_at, updated_at";

    public async ValueTask<Tenant?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM tenants WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            TenantRow.Map, cancellationToken);
        return row?.ToTenant();
    }

    public async ValueTask<IReadOnlyList<Tenant>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM tenants WHERE status = @Status",
            p => p.Add(new NpgsqlParameter("Status", (object)(int)TenantStatus.Active)),
            TenantRow.Map, cancellationToken);
        return rows.Select(r => r.ToTenant()).ToList();
    }

    public async ValueTask UpsertAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        // Enforce: only one Platform tenant allowed
        if (tenant.Type == TenantType.Platform)
        {
            var existing = await _dataSource.QuerySingleOrDefaultAsync(
                "SELECT tenant_id FROM tenants WHERE type = @Type LIMIT 1",
                p => p.Add(new NpgsqlParameter("Type", (object)(int)TenantType.Platform)),
                r => r.GetString("tenant_id"), cancellationToken);
            if (existing is not null && existing != tenant.TenantId)
                throw new InvalidOperationException("Only one Platform tenant is allowed.");
        }

        // Enforce: max depth 3 (Platform -> Partner -> Customer)
        if (tenant.ParentTenantId is not null)
        {
            var parentParentId = await _dataSource.QuerySingleOrDefaultAsync(
                "SELECT parent_tenant_id FROM tenants WHERE tenant_id = @TenantId",
                p => p.Add(new NpgsqlParameter("TenantId", tenant.ParentTenantId)),
                r => r.GetStringOrNull("parent_tenant_id"), cancellationToken);
            if (parentParentId is not null)
            {
                var grandparentParentId = await _dataSource.QuerySingleOrDefaultAsync(
                    "SELECT parent_tenant_id FROM tenants WHERE tenant_id = @TenantId",
                    p => p.Add(new NpgsqlParameter("TenantId", parentParentId)),
                    r => r.GetStringOrNull("parent_tenant_id"), cancellationToken);
                if (grandparentParentId is not null)
                    throw new InvalidOperationException("Maximum tenant hierarchy depth (3 levels) exceeded.");
            }
        }

        var optionsJson = JsonSerializer.Serialize(tenant.Options, PostgresJson.Ctx.TenantOptions);
        var metadataJson = tenant.Metadata is not null
            ? JsonSerializer.Serialize(tenant.Metadata, PostgresJson.Ctx.DictionaryStringString)
            : null;

        await _dataSource.ExecuteAsync(
            "INSERT INTO tenants (tenant_id, name, status, type, parent_tenant_id, options, metadata, created_at, updated_at) " +
            "VALUES (@TenantId, @Name, @Status, @Type, @ParentTenantId, @Options::jsonb, @Metadata::jsonb, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, status = EXCLUDED.status, options = EXCLUDED.options, " +
            "  metadata = EXCLUDED.metadata, updated_at = EXCLUDED.updated_at",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenant.TenantId));
                p.Add(new NpgsqlParameter("Name", tenant.Name));
                p.Add(new NpgsqlParameter("Status", (object)(int)tenant.Status));
                p.Add(new NpgsqlParameter("Type", (object)(int)tenant.Type));
                p.Add(new NpgsqlParameter("ParentTenantId", (object?)tenant.ParentTenantId ?? DBNull.Value));
                p.Add(new NpgsqlParameter("Options", optionsJson));
                p.Add(new NpgsqlParameter("Metadata", (object?)metadataJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("CreatedAt", tenant.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("UpdatedAt", tenant.UpdatedAt.UtcDateTime));
            },
            cancellationToken);
    }

    public async ValueTask UpdateStatusAsync(string tenantId, TenantStatus status, CancellationToken cancellationToken = default)
    {
        // Block deletion if active children exist
        if (status == TenantStatus.Deleted)
        {
            var hasActiveChildren = await _dataSource.ExecuteScalarAsync<bool?>(
                "SELECT EXISTS(SELECT 1 FROM tenants WHERE parent_tenant_id = @TenantId AND status = @ActiveStatus)",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("ActiveStatus", (object)(int)TenantStatus.Active));
                },
                cancellationToken) ?? false;
            if (hasActiveChildren)
                throw new InvalidOperationException("Cannot delete tenant with active children.");
        }

        await _dataSource.ExecuteAsync(
            "UPDATE tenants SET status = @Status, updated_at = @UpdatedAt WHERE tenant_id = @TenantId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("Status", (object)(int)status));
                p.Add(new NpgsqlParameter("UpdatedAt", DateTime.UtcNow));
            },
            cancellationToken);
    }

    public async ValueTask<Tenant?> GetHostTenantAsync(CancellationToken cancellationToken = default)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM tenants WHERE type = @Type",
            p => p.Add(new NpgsqlParameter("Type", (object)(int)TenantType.Platform)),
            TenantRow.Map, cancellationToken);
        return row?.ToTenant();
    }

    public async ValueTask<IReadOnlyList<Tenant>> GetChildrenAsync(string parentTenantId, CancellationToken cancellationToken = default)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM tenants WHERE parent_tenant_id = @ParentTenantId",
            p => p.Add(new NpgsqlParameter("ParentTenantId", parentTenantId)),
            TenantRow.Map, cancellationToken);
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

        public static TenantRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            status = r.GetInt32("status"),
            type = r.GetInt32("type"),
            parent_tenant_id = r.GetStringOrNull("parent_tenant_id"),
            options = r.GetString("options"),
            metadata = r.GetStringOrNull("metadata"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTime("updated_at"),
        };

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
