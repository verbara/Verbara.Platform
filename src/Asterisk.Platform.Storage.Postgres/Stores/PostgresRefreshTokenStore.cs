using Dapper;
using Npgsql;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresRefreshTokenStore : IRefreshTokenStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresRefreshTokenStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(RefreshToken token, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO refresh_tokens (token_id, user_id, tenant_id, token_hash, expires_at, created_at, revoked_at, replaced_by, ip_address, user_agent) " +
            "VALUES (@TokenId, @UserId, @TenantId, @TokenHash, @ExpiresAt, @CreatedAt, @RevokedAt, @ReplacedBy, @IpAddress, @UserAgent)",
            new
            {
                token.TokenId,
                token.UserId,
                token.TenantId,
                token.TokenHash,
                token.ExpiresAt,
                token.CreatedAt,
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
            "SELECT token_id, user_id, tenant_id, token_hash, expires_at, created_at, revoked_at, replaced_by, ip_address, user_agent " +
            "FROM refresh_tokens WHERE token_hash = @TokenHash",
            new { TokenHash = tokenHash });
        return row?.ToRefreshToken();
    }

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RefreshTokenRow>(
            "SELECT token_id, user_id, tenant_id, token_hash, expires_at, created_at, revoked_at, replaced_by, ip_address, user_agent " +
            "FROM refresh_tokens WHERE tenant_id = @TenantId AND user_id = @UserId AND revoked_at IS NULL AND expires_at > now()",
            new { TenantId = tenantId, UserId = userId });
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

    private sealed record RefreshTokenRow(
        string token_id,
        string user_id,
        string tenant_id,
        string token_hash,
        DateTimeOffset expires_at,
        DateTimeOffset created_at,
        DateTimeOffset? revoked_at,
        string? replaced_by,
        string? ip_address,
        string? user_agent)
    {
        public RefreshToken ToRefreshToken() => new()
        {
            TokenId = token_id,
            UserId = user_id,
            TenantId = tenant_id,
            TokenHash = token_hash,
            ExpiresAt = expires_at,
            CreatedAt = created_at,
            RevokedAt = revoked_at,
            ReplacedBy = replaced_by,
            IpAddress = ip_address,
            UserAgent = user_agent,
        };
    }
}
