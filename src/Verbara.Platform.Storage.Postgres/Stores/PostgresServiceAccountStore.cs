using System.Text.Json;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresServiceAccountStore : IServiceAccountStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresServiceAccountStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<ServiceAccount?> GetByIdAsync(TenantId tenantId, EntityId accountId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT account_id, tenant_id, name, description, scopes, is_active, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM service_accounts WHERE tenant_id = @TenantId AND account_id = @AccountId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("AccountId", accountId.Value)); },
            ServiceAccountRow.Map, ct);
        return row?.ToServiceAccount();
    }

    public async Task<PagedResult<ServiceAccount>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var offset = (query.Page - 1) * query.PageSize;

        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM service_accounts WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)), ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT account_id, tenant_id, name, description, scopes, is_active, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM service_accounts WHERE tenant_id = @TenantId " +
            "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", offset)); },
            ServiceAccountRow.Map, ct);

        var items = rows.Select(r => r.ToServiceAccount()).ToList();
        return new PagedResult<ServiceAccount>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(ServiceAccount account, CancellationToken ct)
    {
        var scopesJson = JsonSerializer.Serialize(account.Scopes, PostgresJson.Ctx.IReadOnlyListString);

        await _dataSource.ExecuteAsync(
            "INSERT INTO service_accounts (account_id, tenant_id, name, description, scopes, is_active, " +
            "created_at, updated_at, created_by, updated_by) " +
            "VALUES (@AccountId, @TenantId, @Name, @Description, @Scopes::jsonb, @IsActive, " +
            "@CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, account_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, description = EXCLUDED.description, scopes = EXCLUDED.scopes, " +
            "  is_active = EXCLUDED.is_active, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            p =>
            {
                p.Add(new NpgsqlParameter("AccountId", account.AccountId.Value));
                p.Add(new NpgsqlParameter("TenantId", account.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", account.Name));
                p.Add(new NpgsqlParameter("Description", account.Description));
                p.Add(new NpgsqlParameter("Scopes", scopesJson));
                p.Add(new NpgsqlParameter("IsActive", account.IsActive));
                p.Add(new NpgsqlParameter("CreatedAt", account.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", (object?)account.UpdatedAt ?? DBNull.Value));
                p.Add(new NpgsqlParameter("CreatedBy", (object?)account.CreatedBy ?? DBNull.Value));
                p.Add(new NpgsqlParameter("UpdatedBy", (object?)account.UpdatedBy ?? DBNull.Value));
            },
            ct);
    }

    public async Task DeactivateAsync(TenantId tenantId, EntityId accountId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE service_accounts SET is_active = false WHERE tenant_id = @TenantId AND account_id = @AccountId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("AccountId", accountId.Value)); },
            ct);
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

        public static ServiceAccountRow Map(NpgsqlDataReader r) => new()
        {
            account_id = r.GetString("account_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            description = r.GetString("description"),
            scopes = r.GetString("scopes"),
            is_active = r.GetBoolean("is_active"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
        };

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
