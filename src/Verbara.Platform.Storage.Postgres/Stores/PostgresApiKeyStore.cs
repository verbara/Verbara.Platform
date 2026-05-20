using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresApiKeyStore : IApiKeyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresApiKeyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "key_id, tenant_id, key_hash, name, scopes, rate_limit_per_minute, is_revoked, key_type, created_at, updated_at, expires_at, created_by, updated_by, user_id, last_used_at";

    public async Task<ApiKey?> GetByIdAsync(TenantId tenantId, EntityId keyId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM api_keys WHERE tenant_id = @TenantId AND key_id = @KeyId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("KeyId", keyId.Value)); },
            ApiKeyRow.Map, ct);
        return row?.ToApiKey();
    }

    public async Task<ApiKey?> GetByHashAsync(string hashedKey, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM api_keys WHERE key_hash = @KeyHash",
            p => { p.Add(new NpgsqlParameter("KeyHash", hashedKey)); },
            ApiKeyRow.Map, ct);
        return row?.ToApiKey();
    }

    public async Task<PagedResult<ApiKey>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM api_keys WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)), ct) ?? 0L);
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM api_keys WHERE tenant_id = @TenantId ORDER BY created_at LIMIT @Limit OFFSET @Offset",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", query.Offset)); },
            ApiKeyRow.Map, ct);
        var items = rows.Select(r => r.ToApiKey()).ToList();
        return new PagedResult<ApiKey>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(ApiKey apiKey, CancellationToken ct)
    {
        var scopesCsv = string.Join(",", apiKey.Scopes);
        await _dataSource.ExecuteAsync(
            "INSERT INTO api_keys (key_id, tenant_id, key_hash, name, scopes, rate_limit_per_minute, is_revoked, key_type, created_at, updated_at, expires_at, created_by, updated_by, user_id) " +
            "VALUES (@KeyId, @TenantId, @KeyHash, @Name, @Scopes, @RateLimitPerMinute, @IsRevoked, @KeyType, @CreatedAt, @UpdatedAt, @ExpiresAt, @CreatedBy, @UpdatedBy, @UserId) " +
            "ON CONFLICT (tenant_id, key_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, scopes = EXCLUDED.scopes, rate_limit_per_minute = EXCLUDED.rate_limit_per_minute, " +
            "  is_revoked = EXCLUDED.is_revoked, key_type = EXCLUDED.key_type, updated_at = EXCLUDED.updated_at, expires_at = EXCLUDED.expires_at, " +
            "  updated_by = EXCLUDED.updated_by, user_id = EXCLUDED.user_id",
            p =>
            {
                p.Add(new NpgsqlParameter("KeyId", apiKey.KeyId.Value));
                p.Add(new NpgsqlParameter("TenantId", apiKey.TenantId.Value));
                p.Add(new NpgsqlParameter("KeyHash", apiKey.HashedKey));
                p.Add(new NpgsqlParameter("Name", apiKey.Name));
                p.Add(new NpgsqlParameter("Scopes", scopesCsv));
                p.Add(new NpgsqlParameter("RateLimitPerMinute", (object?)apiKey.RateLimitPerMinute ?? DBNull.Value));
                p.Add(new NpgsqlParameter("IsRevoked", apiKey.IsRevoked));
                p.Add(new NpgsqlParameter("KeyType", (int)apiKey.KeyType));
                p.Add(new NpgsqlParameter("CreatedAt", apiKey.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", (object?)apiKey.UpdatedAt ?? DBNull.Value));
                p.Add(new NpgsqlParameter("ExpiresAt", (object?)apiKey.ExpiresAt ?? DBNull.Value));
                p.Add(new NpgsqlParameter("CreatedBy", (object?)apiKey.CreatedBy ?? DBNull.Value));
                p.Add(new NpgsqlParameter("UpdatedBy", (object?)apiKey.UpdatedBy ?? DBNull.Value));
                p.Add(new NpgsqlParameter("UserId", (object?)apiKey.UserId?.Value ?? DBNull.Value));
            },
            ct);
    }

    public async Task RevokeAsync(TenantId tenantId, EntityId keyId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE api_keys SET is_revoked = true WHERE tenant_id = @TenantId AND key_id = @KeyId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("KeyId", keyId.Value)); },
            ct);
    }

    public async Task UpdateLastUsedAsync(EntityId keyId, DateTimeOffset usedAt, CancellationToken ct)
    {
        // Tenant-scoped lookup is intentionally skipped: the auth middleware
        // already resolved the row by hash and the (tenant_id, key_id) pair is
        // unique. Stamping a global UPDATE keeps the hot path single-statement.
        await _dataSource.ExecuteAsync(
            "UPDATE api_keys SET last_used_at = @UsedAt WHERE key_id = @KeyId",
            p => { p.Add(new NpgsqlParameter("KeyId", keyId.Value)); p.Add(new NpgsqlParameter("UsedAt", usedAt)); },
            ct);
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
        public DateTime? last_used_at { get; init; }

        public static ApiKeyRow Map(NpgsqlDataReader r) => new()
        {
            key_id = r.GetString("key_id"),
            tenant_id = r.GetString("tenant_id"),
            key_hash = r.GetString("key_hash"),
            name = r.GetString("name"),
            scopes = r.GetString("scopes"),
            rate_limit_per_minute = r.GetInt32OrNull("rate_limit_per_minute"),
            is_revoked = r.GetBoolean("is_revoked"),
            key_type = r.GetInt32("key_type"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            expires_at = r.GetDateTimeOrNull("expires_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
            user_id = r.GetStringOrNull("user_id"),
            last_used_at = r.GetDateTimeOrNull("last_used_at"),
        };

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
                LastUsedAt = last_used_at,
            };
        }
    }
}
