using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Mail.Services;

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

internal class TokenStore
{
    private const string SelectColumns =
        "id, tenant_id, user_id, provider, access_token, refresh_token, expires_at, scopes, created_at, updated_at";

    private readonly NpgsqlDataSource? _dataSource;

    public TokenStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Test-only parameterless constructor — concrete subclasses override the async methods
    /// to supply in-memory fakes. Production use goes through the <c>NpgsqlDataSource</c> ctor.
    /// </summary>
    protected TokenStore()
    {
        _dataSource = null;
    }

    public virtual async Task<OAuthToken?> GetAsync(string tenantId, string userId, string provider, CancellationToken ct)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM mail.oauth_tokens
            WHERE tenant_id = @TenantId AND user_id = @UserId AND provider = @Provider
            """;

        return await _dataSource!.QuerySingleOrDefaultAsync(
            sql,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("UserId", userId));
                p.Add(new NpgsqlParameter("Provider", provider));
            },
            MapToken,
            ct);
    }

    public virtual async Task UpsertAsync(OAuthToken token, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO mail.oauth_tokens (id, tenant_id, user_id, provider, access_token, refresh_token, expires_at, scopes, created_at, updated_at)
            VALUES (@Id, @TenantId, @UserId, @Provider, @AccessToken, @RefreshToken, @ExpiresAt, @Scopes, @CreatedAt, @UpdatedAt)
            ON CONFLICT (tenant_id, user_id, provider)
            DO UPDATE SET access_token = @AccessToken, refresh_token = @RefreshToken, expires_at = @ExpiresAt,
                          scopes = @Scopes, updated_at = @UpdatedAt
            """;

        await _dataSource!.ExecuteAsync(
            sql,
            p =>
            {
                p.Add(new NpgsqlParameter("Id", token.Id));
                p.Add(new NpgsqlParameter("TenantId", token.TenantId));
                p.Add(new NpgsqlParameter("UserId", token.UserId));
                p.Add(new NpgsqlParameter("Provider", token.Provider));
                p.Add(new NpgsqlParameter("AccessToken", token.AccessToken));
                p.Add(new NpgsqlParameter("RefreshToken", token.RefreshToken));
                p.Add(new NpgsqlParameter("ExpiresAt", token.ExpiresAt));
                p.Add(new NpgsqlParameter("Scopes", token.Scopes));
                p.Add(new NpgsqlParameter("CreatedAt", token.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", token.UpdatedAt));
            },
            ct);
    }

    public virtual async Task DeleteAsync(string tenantId, string userId, string provider, CancellationToken ct)
    {
        const string sql = "DELETE FROM mail.oauth_tokens WHERE tenant_id = @TenantId AND user_id = @UserId AND provider = @Provider";

        await _dataSource!.ExecuteAsync(
            sql,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("UserId", userId));
                p.Add(new NpgsqlParameter("Provider", provider));
            },
            ct);
    }

    public virtual async Task<IReadOnlyList<OAuthToken>> GetExpiringAsync(TimeSpan window, CancellationToken ct)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM mail.oauth_tokens
            WHERE expires_at <= @Threshold AND refresh_token IS NOT NULL AND refresh_token != ''
            ORDER BY expires_at ASC
            """;

        var threshold = DateTime.UtcNow.Add(window);
        return await _dataSource!.QueryListAsync(
            sql,
            p => p.Add(new NpgsqlParameter("Threshold", threshold)),
            MapToken,
            ct);
    }

    private static OAuthToken MapToken(NpgsqlDataReader r) => new()
    {
        Id = r.GetString("id"),
        TenantId = r.GetString("tenant_id"),
        UserId = r.GetString("user_id"),
        Provider = r.GetString("provider"),
        AccessToken = r.GetString("access_token"),
        RefreshToken = r.GetString("refresh_token"),
        ExpiresAt = r.GetDateTime("expires_at"),
        Scopes = r.GetString("scopes"),
        CreatedAt = r.GetDateTime("created_at"),
        UpdatedAt = r.GetDateTime("updated_at"),
    };
}
