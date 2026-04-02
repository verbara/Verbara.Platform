using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresServiceAccountStore : IServiceAccountStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresServiceAccountStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<ServiceAccount?> GetByIdAsync(TenantId tenantId, EntityId accountId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ServiceAccountRow>(
            "SELECT account_id, tenant_id, name, description, scopes, is_active, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM service_accounts WHERE tenant_id = @TenantId AND account_id = @AccountId",
            new { TenantId = tenantId.Value, AccountId = accountId.Value });
        return row?.ToServiceAccount();
    }

    public async Task<PagedResult<ServiceAccount>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var offset = (query.Page - 1) * query.PageSize;

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM service_accounts WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });

        var rows = await conn.QueryAsync<ServiceAccountRow>(
            "SELECT account_id, tenant_id, name, description, scopes, is_active, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM service_accounts WHERE tenant_id = @TenantId " +
            "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId.Value, Limit = query.PageSize, Offset = offset });

        var items = rows.Select(r => r.ToServiceAccount()).ToList();
        return new PagedResult<ServiceAccount>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(ServiceAccount account, CancellationToken ct)
    {
        var scopesJson = JsonSerializer.Serialize(account.Scopes, PostgresJson.Ctx.IReadOnlyListString);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO service_accounts (account_id, tenant_id, name, description, scopes, is_active, " +
            "created_at, updated_at, created_by, updated_by) " +
            "VALUES (@AccountId, @TenantId, @Name, @Description, @Scopes::jsonb, @IsActive, " +
            "@CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, account_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, description = EXCLUDED.description, scopes = EXCLUDED.scopes, " +
            "  is_active = EXCLUDED.is_active, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            new
            {
                AccountId = account.AccountId.Value,
                TenantId = account.TenantId.Value,
                account.Name,
                account.Description,
                Scopes = scopesJson,
                account.IsActive,
                account.CreatedAt,
                account.UpdatedAt,
                account.CreatedBy,
                account.UpdatedBy,
            });
    }

    public async Task DeactivateAsync(TenantId tenantId, EntityId accountId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE service_accounts SET is_active = false WHERE tenant_id = @TenantId AND account_id = @AccountId",
            new { TenantId = tenantId.Value, AccountId = accountId.Value });
    }

    private sealed class ServiceAccountRow
    {
        public string account_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public string description { get; init; } = null!;
        public string scopes { get; init; } = null!;
        public bool is_active { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }

        public ServiceAccount ToServiceAccount()
        {
            var scopeList = JsonSerializer.Deserialize(scopes, PostgresJson.Ctx.IReadOnlyListString)
                            ?? (IReadOnlyList<string>)[];

            return new ServiceAccount
            {
                AccountId = EntityId.From(account_id),
                TenantId = new TenantId(tenant_id),
                Name = name,
                Description = description,
                Scopes = scopeList,
                IsActive = is_active,
                CreatedAt = created_at,
                UpdatedAt = updated_at,
                CreatedBy = created_by,
                UpdatedBy = updated_by,
            };
        }
    }
}
