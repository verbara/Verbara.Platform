using Npgsql;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantIpAllowlistStore : ITenantIpAllowlistStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantIpAllowlistStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT id, tenant_id, cidr::text AS cidr, description, created_at, created_by_user_id " +
            "FROM tenant_ip_allowlist WHERE tenant_id = @TenantId ORDER BY created_at",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            IpAllowlistRow.Map, ct);
        return rows.Select(r => r.ToEntry()).ToArray();
    }

    public async Task<IpAllowlistEntry> AddAsync(
        string tenantId,
        string cidr,
        string? description,
        string? createdByUserId,
        CancellationToken ct)
    {
        try
        {
            var row = await _dataSource.QuerySingleAsync(
                "INSERT INTO tenant_ip_allowlist (tenant_id, cidr, description, created_by_user_id) " +
                "VALUES (@TenantId, @Cidr::cidr, @Description, @CreatedByUserId) " +
                "RETURNING id, tenant_id, cidr::text AS cidr, description, created_at, created_by_user_id",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("Cidr", cidr));
                    p.Add(new NpgsqlParameter("Description", (object?)description ?? DBNull.Value));
                    p.Add(new NpgsqlParameter("CreatedByUserId", (object?)createdByUserId ?? DBNull.Value));
                },
                IpAllowlistRow.Map, ct);
            return row.ToEntry();
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Unique violation on (tenant_id, cidr) — return the existing row instead of throwing.
            var existing = await _dataSource.QuerySingleAsync(
                "SELECT id, tenant_id, cidr::text AS cidr, description, created_at, created_by_user_id " +
                "FROM tenant_ip_allowlist WHERE tenant_id = @TenantId AND cidr = @Cidr::cidr",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("Cidr", cidr));
                },
                IpAllowlistRow.Map, ct);
            return existing.ToEntry();
        }
    }

    public async Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct)
    {
        var rows = await _dataSource.ExecuteAsync(
            "DELETE FROM tenant_ip_allowlist WHERE tenant_id = @TenantId AND id = @Id",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("Id", entryId));
            },
            ct);
        return rows > 0;
    }

    public async Task<int> CountAsync(string tenantId, CancellationToken ct)
    {
        return (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM tenant_ip_allowlist WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)), ct) ?? 0L);
    }

    private sealed class IpAllowlistRow
    {
        public Guid id { get; init; }
        public string tenant_id { get; init; } = null!;
        public string cidr { get; init; } = null!;
        public string? description { get; init; }
        public DateTimeOffset created_at { get; init; }
        public string? created_by_user_id { get; init; }

        public static IpAllowlistRow Map(NpgsqlDataReader r) => new()
        {
            id = r.GetGuid("id"),
            tenant_id = r.GetString("tenant_id"),
            cidr = r.GetString("cidr"),
            description = r.GetStringOrNull("description"),
            created_at = r.GetDateTimeOffset("created_at"),
            created_by_user_id = r.GetStringOrNull("created_by_user_id"),
        };

        public IpAllowlistEntry ToEntry() => new()
        {
            Id = id,
            TenantId = tenant_id,
            Cidr = cidr,
            Description = description,
            CreatedAt = created_at,
            CreatedByUserId = created_by_user_id,
        };
    }
}
