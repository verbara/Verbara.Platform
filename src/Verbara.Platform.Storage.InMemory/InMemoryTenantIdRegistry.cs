using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// <see cref="ITenantIdRegistry"/> over the in-memory tenant store (realtime-objects-stay-in-their-tenant, design D1):
/// the overlap check and the insert run under one lock.
/// </summary>
public sealed class InMemoryTenantIdRegistry : ITenantIdRegistry
{
    private readonly ITenantStore _store;

    public InMemoryTenantIdRegistry(ITenantStore store) => _store = store;

    public ValueTask<TenantCreateResult> TryCreateAsync(Tenant tenant, CancellationToken ct) => throw new NotImplementedException();

    public ValueTask<IReadOnlyList<StoredTenantId>> ListAllIdsAsync(CancellationToken ct) => throw new NotImplementedException();
}
