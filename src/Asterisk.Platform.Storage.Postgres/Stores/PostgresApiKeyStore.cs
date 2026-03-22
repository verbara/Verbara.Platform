using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresApiKeyStore : IApiKeyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresApiKeyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<ApiKey?> GetByIdAsync(TenantId tenantId, EntityId keyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ApiKeyRow>(
            "SELECT key_id, tenant_id, key_hash, name, scopes, rate_limit_per_minute, is_revoked, created_at, updated_at, expires_at, created_by, updated_by " +
            "FROM api_keys WHERE tenant_id = @TenantId AND key_id = @KeyId",
            new { TenantId = tenantId.Value, KeyId = keyId.Value });
        return row?.ToApiKey();
    }

    public async Task<ApiKey?> GetByHashAsync(string hashedKey, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ApiKeyRow>(
            "SELECT key_id, tenant_id, key_hash, name, scopes, rate_limit_per_minute, is_revoked, created_at, updated_at, expires_at, created_by, updated_by " +
            "FROM api_keys WHERE key_hash = @KeyHash",
            new { KeyHash = hashedKey });
        return row?.ToApiKey();
    }

    public async Task<PagedResult<ApiKey>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM api_keys WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });
        var rows = await conn.QueryAsync<ApiKeyRow>(
            "SELECT key_id, tenant_id, key_hash, name, scopes, rate_limit_per_minute, is_revoked, created_at, updated_at, expires_at, created_by, updated_by " +
            "FROM api_keys WHERE tenant_id = @TenantId ORDER BY created_at LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId.Value, Limit = query.PageSize, Offset = query.Offset });
        var items = rows.Select(r => r.ToApiKey()).ToList();
        return new PagedResult<ApiKey>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(ApiKey apiKey, CancellationToken ct)
    {
        var scopesCsv = string.Join(",", apiKey.Scopes);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO api_keys (key_id, tenant_id, key_hash, name, scopes, rate_limit_per_minute, is_revoked, created_at, updated_at, expires_at, created_by, updated_by) " +
            "VALUES (@KeyId, @TenantId, @KeyHash, @Name, @Scopes, @RateLimitPerMinute, @IsRevoked, @CreatedAt, @UpdatedAt, @ExpiresAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, key_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, scopes = EXCLUDED.scopes, rate_limit_per_minute = EXCLUDED.rate_limit_per_minute, " +
            "  is_revoked = EXCLUDED.is_revoked, updated_at = EXCLUDED.updated_at, expires_at = EXCLUDED.expires_at, updated_by = EXCLUDED.updated_by",
            new
            {
                KeyId = apiKey.KeyId.Value,
                TenantId = apiKey.TenantId.Value,
                KeyHash = apiKey.HashedKey,
                apiKey.Name,
                Scopes = scopesCsv,
                apiKey.RateLimitPerMinute,
                apiKey.IsRevoked,
                apiKey.CreatedAt,
                apiKey.UpdatedAt,
                apiKey.ExpiresAt,
                apiKey.CreatedBy,
                apiKey.UpdatedBy,
            });
    }

    public async Task RevokeAsync(TenantId tenantId, EntityId keyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE api_keys SET is_revoked = true WHERE tenant_id = @TenantId AND key_id = @KeyId",
            new { TenantId = tenantId.Value, KeyId = keyId.Value });
    }

    private sealed record ApiKeyRow(
        string key_id,
        string tenant_id,
        string key_hash,
        string name,
        string scopes,
        int? rate_limit_per_minute,
        bool is_revoked,
        DateTimeOffset created_at,
        DateTimeOffset? updated_at,
        DateTimeOffset? expires_at,
        string? created_by,
        string? updated_by)
    {
        public ApiKey ToApiKey()
        {
            var scopeList = string.IsNullOrEmpty(scopes)
                ? (IReadOnlyList<string>)[]
                : (IReadOnlyList<string>)scopes.Split(',');
            return new ApiKey
            {
                KeyId = EntityId.From(key_id),
                TenantId = new TenantId(tenant_id),
                HashedKey = key_hash,
                Name = name,
                Scopes = scopeList,
                RateLimitPerMinute = rate_limit_per_minute,
                IsRevoked = is_revoked,
                CreatedAt = created_at,
                UpdatedAt = updated_at,
                ExpiresAt = expires_at,
                CreatedBy = created_by,
                UpdatedBy = updated_by,
            };
        }
    }
}
