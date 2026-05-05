using Dapper;
using Npgsql;
using Verbara.Platform.Identity;

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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO refresh_tokens (token_id, user_id, tenant_id, token_hash, expires_at, created_at, last_activity_at, revoked_at, replaced_by, ip_address, user_agent) " +
            "VALUES (@TokenId, @UserId, @TenantId, @TokenHash, @ExpiresAt, @CreatedAt, @LastActivityAt, @RevokedAt, @ReplacedBy, @IpAddress, @UserAgent)",
            new
            {
                token.TokenId,
                token.UserId,
                token.TenantId,
                token.TokenHash,
                token.ExpiresAt,
                token.CreatedAt,
                token.LastActivityAt,
                token.RevokedAt,
                token.ReplacedBy,
                token.IpAddress,
                token.UserAgent,
            });
    }

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RefreshTokenRow>(
            $"SELECT {SelectColumns} FROM refresh_tokens WHERE token_hash = @TokenHash",
            new { TokenHash = tokenHash });
        return row?.ToRefreshToken();
    }

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RefreshTokenRow>(
            $"SELECT {SelectColumns} " +
            "FROM refresh_tokens WHERE tenant_id = @TenantId AND user_id = @UserId AND revoked_at IS NULL AND expires_at > now()",
            new { TenantId = tenantId, UserId = userId });
        return rows.Select(r => r.ToRefreshToken()).ToList();
    }

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RefreshTokenRow>(
            $"SELECT {SelectColumns} " +
            "FROM refresh_tokens WHERE tenant_id = @TenantId AND revoked_at IS NULL AND expires_at > now()",
            new { TenantId = tenantId });
        return rows.Select(r => r.ToRefreshToken()).ToList();
    }

    public async Task RevokeAsync(string tokenId, DateTimeOffset revokedAt, string? replacedBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE refresh_tokens SET revoked_at = @RevokedAt, replaced_by = @ReplacedBy WHERE token_id = @TokenId",
            new { TokenId = tokenId, RevokedAt = revokedAt, ReplacedBy = replacedBy });
    }

    public async Task RevokeAllForUserAsync(string tenantId, string userId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE refresh_tokens SET revoked_at = @RevokedAt WHERE tenant_id = @TenantId AND user_id = @UserId AND revoked_at IS NULL",
            new { TenantId = tenantId, UserId = userId, RevokedAt = revokedAt });
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

        public RefreshToken ToRefreshToken() => new()
        {
            TokenId = token_id,
            UserId = user_id,
            TenantId = tenant_id,
            TokenHash = token_hash,
            ExpiresAt = expires_at,
            CreatedAt = created_at,
            LastActivityAt = last_activity_at,
            RevokedAt = revoked_at,
            ReplacedBy = replaced_by,
            IpAddress = ip_address,
            UserAgent = user_agent,
        };
    }
}
