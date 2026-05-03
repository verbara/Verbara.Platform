using Dapper;
using Npgsql;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantIpAllowlistStore : ITenantIpAllowlistStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantIpAllowlistStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<IpAllowlistRow>(
            "SELECT id, tenant_id, cidr::text AS cidr, description, created_at, created_by_user_id " +
            "FROM tenant_ip_allowlist WHERE tenant_id = @TenantId ORDER BY created_at",
            new { TenantId = tenantId });
        return rows.Select(r => r.ToEntry()).ToArray();
    }

    public async Task<IpAllowlistEntry> AddAsync(
        string tenantId,
        string cidr,
        string? description,
        string? createdByUserId,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var row = await conn.QuerySingleAsync<IpAllowlistRow>(
                "INSERT INTO tenant_ip_allowlist (tenant_id, cidr, description, created_by_user_id) " +
                "VALUES (@TenantId, @Cidr::cidr, @Description, @CreatedByUserId) " +
                "RETURNING id, tenant_id, cidr::text AS cidr, description, created_at, created_by_user_id",
                new { TenantId = tenantId, Cidr = cidr, Description = description, CreatedByUserId = createdByUserId });
            return row.ToEntry();
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Unique violation on (tenant_id, cidr) — return the existing row instead of throwing.
            var existing = await conn.QuerySingleAsync<IpAllowlistRow>(
                "SELECT id, tenant_id, cidr::text AS cidr, description, created_at, created_by_user_id " +
                "FROM tenant_ip_allowlist WHERE tenant_id = @TenantId AND cidr = @Cidr::cidr",
                new { TenantId = tenantId, Cidr = cidr });
            return existing.ToEntry();
        }
    }

    public async Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            "DELETE FROM tenant_ip_allowlist WHERE tenant_id = @TenantId AND id = @Id",
            new { TenantId = tenantId, Id = entryId });
        return rows > 0;
    }

    public async Task<int> CountAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tenant_ip_allowlist WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
    }

    private sealed class IpAllowlistRow
    {
        public Guid id { get; init; }
        public string tenant_id { get; init; } = null!;
        public string cidr { get; init; } = null!;
        public string? description { get; init; }
        public DateTimeOffset created_at { get; init; }
        public string? created_by_user_id { get; init; }

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
