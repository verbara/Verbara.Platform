using Npgsql;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// <see cref="ITenantIdRegistry"/> over the <c>tenants</c> table (realtime-objects-stay-in-their-tenant, design D1):
/// one transaction at READ COMMITTED takes a transaction-scoped advisory lock on a constant key, scans every stored
/// id for an overlap, and inserts only when there is none.
/// </summary>
public sealed class PostgresTenantIdRegistry : ITenantIdRegistry
{
    /// <summary>The advisory-lock key every tenant create takes (<c>0x74656E616E746964</c>, "tenantid" in ASCII).</summary>
    public const long LockKey = 0x74656E616E746964;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantIdRegistry(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public ValueTask<TenantCreateResult> TryCreateAsync(Tenant tenant, CancellationToken ct) => throw new NotImplementedException();

    public ValueTask<IReadOnlyList<StoredTenantId>> ListAllIdsAsync(CancellationToken ct) => throw new NotImplementedException();
}
