using Dapper;
using Npgsql;

namespace Asterisk.Platform.Mail.Services;

internal sealed class OAuthToken
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required string Provider { get; init; }
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required string Scopes { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

internal sealed class TokenStore
{
    private readonly NpgsqlDataSource _dataSource;

    public TokenStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<OAuthToken?> GetAsync(string tenantId, string userId, string provider, CancellationToken ct)
    {
        const string sql = """
            SELECT id, tenant_id AS TenantId, user_id AS UserId, provider, access_token AS AccessToken,
                   refresh_token AS RefreshToken, expires_at AS ExpiresAt, scopes,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM mail.oauth_tokens
            WHERE tenant_id = @TenantId AND user_id = @UserId AND provider = @Provider
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<OAuthTokenRow>(sql, new { TenantId = tenantId, UserId = userId, Provider = provider });
        return row?.ToModel();
    }

    public async Task UpsertAsync(OAuthToken token, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO mail.oauth_tokens (id, tenant_id, user_id, provider, access_token, refresh_token, expires_at, scopes, created_at, updated_at)
            VALUES (@Id, @TenantId, @UserId, @Provider, @AccessToken, @RefreshToken, @ExpiresAt, @Scopes, @CreatedAt, @UpdatedAt)
            ON CONFLICT (tenant_id, user_id, provider)
            DO UPDATE SET access_token = @AccessToken, refresh_token = @RefreshToken, expires_at = @ExpiresAt,
                          scopes = @Scopes, updated_at = @UpdatedAt
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(sql, new
        {
            token.Id,
            token.TenantId,
            token.UserId,
            token.Provider,
            token.AccessToken,
            token.RefreshToken,
            token.ExpiresAt,
            token.Scopes,
            token.CreatedAt,
            token.UpdatedAt,
        });
    }

    public async Task DeleteAsync(string tenantId, string userId, string provider, CancellationToken ct)
    {
        const string sql = "DELETE FROM mail.oauth_tokens WHERE tenant_id = @TenantId AND user_id = @UserId AND provider = @Provider";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(sql, new { TenantId = tenantId, UserId = userId, Provider = provider });
    }

    public async Task<IReadOnlyList<OAuthToken>> GetExpiringAsync(TimeSpan window, CancellationToken ct)
    {
        const string sql = """
            SELECT id, tenant_id AS TenantId, user_id AS UserId, provider, access_token AS AccessToken,
                   refresh_token AS RefreshToken, expires_at AS ExpiresAt, scopes,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM mail.oauth_tokens
            WHERE expires_at <= @Threshold AND refresh_token IS NOT NULL AND refresh_token != ''
            ORDER BY expires_at ASC
            """;

        var threshold = DateTime.UtcNow.Add(window);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<OAuthTokenRow>(sql, new { Threshold = threshold });
        return rows.Select(r => r.ToModel()).ToList();
    }

    private sealed class OAuthTokenRow
    {
        public string Id { get; init; } = string.Empty;
        public string TenantId { get; init; } = string.Empty;
        public string UserId { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public string AccessToken { get; init; } = string.Empty;
        public string RefreshToken { get; init; } = string.Empty;
        public DateTime ExpiresAt { get; init; }
        public string Scopes { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }

        public OAuthToken ToModel() => new()
        {
            Id = Id,
            TenantId = TenantId,
            UserId = UserId,
            Provider = Provider,
            AccessToken = AccessToken,
            RefreshToken = RefreshToken,
            ExpiresAt = ExpiresAt,
            Scopes = Scopes,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
