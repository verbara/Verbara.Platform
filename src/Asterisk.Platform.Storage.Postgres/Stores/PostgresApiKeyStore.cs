using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresApiKeyStore : IApiKeyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresApiKeyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "key_id, tenant_id, key_hash, name, scopes, rate_limit_per_minute, is_revoked, key_type, created_at, updated_at, expires_at, created_by, updated_by, user_id";

    public async Task<ApiKey?> GetByIdAsync(TenantId tenantId, EntityId keyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ApiKeyRow>(
            $"SELECT {SelectColumns} FROM api_keys WHERE tenant_id = @TenantId AND key_id = @KeyId",
            new { TenantId = tenantId.Value, KeyId = keyId.Value });
        return row?.ToApiKey();
    }

    public async Task<ApiKey?> GetByHashAsync(string hashedKey, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ApiKeyRow>(
            $"SELECT {SelectColumns} FROM api_keys WHERE key_hash = @KeyHash",
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
            $"SELECT {SelectColumns} FROM api_keys WHERE tenant_id = @TenantId ORDER BY created_at LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId.Value, Limit = query.PageSize, Offset = query.Offset });
        var items = rows.Select(r => r.ToApiKey()).ToList();
        return new PagedResult<ApiKey>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(ApiKey apiKey, CancellationToken ct)
    {
        var scopesCsv = string.Join(",", apiKey.Scopes);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO api_keys (key_id, tenant_id, key_hash, name, scopes, rate_limit_per_minute, is_revoked, key_type, created_at, updated_at, expires_at, created_by, updated_by, user_id) " +
            "VALUES (@KeyId, @TenantId, @KeyHash, @Name, @Scopes, @RateLimitPerMinute, @IsRevoked, @KeyType, @CreatedAt, @UpdatedAt, @ExpiresAt, @CreatedBy, @UpdatedBy, @UserId) " +
            "ON CONFLICT (tenant_id, key_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, scopes = EXCLUDED.scopes, rate_limit_per_minute = EXCLUDED.rate_limit_per_minute, " +
            "  is_revoked = EXCLUDED.is_revoked, key_type = EXCLUDED.key_type, updated_at = EXCLUDED.updated_at, expires_at = EXCLUDED.expires_at, " +
            "  updated_by = EXCLUDED.updated_by, user_id = EXCLUDED.user_id",
            new
            {
                KeyId = apiKey.KeyId.Value,
                TenantId = apiKey.TenantId.Value,
                KeyHash = apiKey.HashedKey,
                apiKey.Name,
                Scopes = scopesCsv,
                apiKey.RateLimitPerMinute,
                apiKey.IsRevoked,
                KeyType = (int)apiKey.KeyType,
                apiKey.CreatedAt,
                apiKey.UpdatedAt,
                apiKey.ExpiresAt,
                apiKey.CreatedBy,
                apiKey.UpdatedBy,
                UserId = apiKey.UserId?.Value,
            });
    }

    public async Task RevokeAsync(TenantId tenantId, EntityId keyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE api_keys SET is_revoked = true WHERE tenant_id = @TenantId AND key_id = @KeyId",
            new { TenantId = tenantId.Value, KeyId = keyId.Value });
    }

    private sealed class ApiKeyRow
    {
        public string key_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string key_hash { get; init; } = null!;
        public string name { get; init; } = null!;
        public string scopes { get; init; } = null!;
        public int? rate_limit_per_minute { get; init; }
        public bool is_revoked { get; init; }
        public int key_type { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }
        public DateTime? expires_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }
        public string? user_id { get; init; }

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
                UserId = user_id is not null ? EntityId.From(user_id) : null,
                KeyType = (ApiKeyType)key_type,
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
