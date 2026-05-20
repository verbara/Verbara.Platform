using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresRefreshTokenStore : IRefreshTokenStore
{
    private const string SelectColumns =
        "token_id, user_id, tenant_id, token_hash, expires_at, created_at, last_activity_at, " +
        "revoked_at, replaced_by, ip_address, user_agent";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresRefreshTokenStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(RefreshToken token, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO refresh_tokens (token_id, user_id, tenant_id, token_hash, expires_at, created_at, last_activity_at, revoked_at, replaced_by, ip_address, user_agent) " +
            "VALUES (@TokenId, @UserId, @TenantId, @TokenHash, @ExpiresAt, @CreatedAt, @LastActivityAt, @RevokedAt, @ReplacedBy, @IpAddress, @UserAgent)",
            p =>
            {
                p.Add(new NpgsqlParameter("TokenId", token.TokenId));
                p.Add(new NpgsqlParameter("UserId", token.UserId));
                p.Add(new NpgsqlParameter("TenantId", token.TenantId));
                p.Add(new NpgsqlParameter("TokenHash", token.TokenHash));
                p.Add(new NpgsqlParameter("ExpiresAt", token.ExpiresAt.UtcDateTime));
                p.Add(new NpgsqlParameter("CreatedAt", token.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("LastActivityAt", token.LastActivityAt.UtcDateTime));
                p.Add(new NpgsqlParameter("RevokedAt", NpgsqlDbType.TimestampTz) { Value = (object?)token.RevokedAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ReplacedBy", NpgsqlDbType.Text) { Value = (object?)token.ReplacedBy ?? DBNull.Value });
                p.Add(new NpgsqlParameter("IpAddress", NpgsqlDbType.Text) { Value = (object?)token.IpAddress ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UserAgent", NpgsqlDbType.Text) { Value = (object?)token.UserAgent ?? DBNull.Value });
            },
            ct);
    }

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM refresh_tokens WHERE token_hash = @TokenHash",
            p => p.Add(new NpgsqlParameter("TokenHash", tokenHash)),
            RefreshTokenRow.Map, ct);
        return row?.ToRefreshToken();
    }

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} " +
            "FROM refresh_tokens WHERE tenant_id = @TenantId AND user_id = @UserId AND revoked_at IS NULL AND expires_at > now()",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId)); p.Add(new NpgsqlParameter("UserId", userId)); },
            RefreshTokenRow.Map, ct);
        return rows.Select(r => r.ToRefreshToken()).ToList();
    }

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} " +
            "FROM refresh_tokens WHERE tenant_id = @TenantId AND revoked_at IS NULL AND expires_at > now()",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            RefreshTokenRow.Map, ct);
        return rows.Select(r => r.ToRefreshToken()).ToList();
    }

    public async Task RevokeAsync(string tokenId, DateTimeOffset revokedAt, string? replacedBy, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE refresh_tokens SET revoked_at = @RevokedAt, replaced_by = @ReplacedBy WHERE token_id = @TokenId",
            p =>
            {
                p.Add(new NpgsqlParameter("TokenId", tokenId));
                p.Add(new NpgsqlParameter("RevokedAt", revokedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("ReplacedBy", NpgsqlDbType.Text) { Value = (object?)replacedBy ?? DBNull.Value });
            },
            ct);
    }

    public async Task RevokeAllForUserAsync(string tenantId, string userId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE refresh_tokens SET revoked_at = @RevokedAt WHERE tenant_id = @TenantId AND user_id = @UserId AND revoked_at IS NULL",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("UserId", userId));
                p.Add(new NpgsqlParameter("RevokedAt", revokedAt.UtcDateTime));
            },
            ct);
    }

    private sealed class RefreshTokenRow
    {
        public string token_id { get; init; } = null!;
        public string user_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string token_hash { get; init; } = null!;
        public DateTime expires_at { get; init; }
        public DateTime created_at { get; init; }
        public DateTime last_activity_at { get; init; }
        public DateTime? revoked_at { get; init; }
        public string? replaced_by { get; init; }
        public string? ip_address { get; init; }
        public string? user_agent { get; init; }

        public static RefreshTokenRow Map(NpgsqlDataReader r) => new()
        {
            token_id = r.GetString("token_id"),
            user_id = r.GetString("user_id"),
            tenant_id = r.GetString("tenant_id"),
            token_hash = r.GetString("token_hash"),
            expires_at = r.GetDateTime("expires_at"),
            created_at = r.GetDateTime("created_at"),
            last_activity_at = r.GetDateTime("last_activity_at"),
            revoked_at = r.GetDateTimeOrNull("revoked_at"),
            replaced_by = r.GetStringOrNull("replaced_by"),
            ip_address = r.GetStringOrNull("ip_address"),
            user_agent = r.GetStringOrNull("user_agent"),
        };

        public RefreshToken ToRefreshToken() => new()
        {
            TokenId = token_id,
            UserId = user_id,
            TenantId = tenant_id,
            TokenHash = token_hash,
            ExpiresAt = new DateTimeOffset(expires_at, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
            LastActivityAt = new DateTimeOffset(last_activity_at, TimeSpan.Zero),
            RevokedAt = revoked_at.HasValue ? new DateTimeOffset(revoked_at.Value, TimeSpan.Zero) : null,
            ReplacedBy = replaced_by,
            IpAddress = ip_address,
            UserAgent = user_agent,
        };
    }
}
