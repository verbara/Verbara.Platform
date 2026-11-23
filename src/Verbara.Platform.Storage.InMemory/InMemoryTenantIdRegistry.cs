using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// <see cref="ITenantIdRegistry"/> over the in-memory tenant store (realtime-objects-stay-in-their-tenant, design D1):
/// the overlap check and the insert run under one lock.
/// </summary>
/// <remarks>
/// Over an <see cref="InMemoryTenantStore"/> every stored tenant is seen. Over any other <see cref="ITenantStore"/>
/// (a test double) only what <see cref="ITenantStore.GetAsync"/> and <see cref="ITenantStore.GetAllActiveAsync"/>
/// return is seen.
/// </remarks>
public sealed class InMemoryTenantIdRegistry : ITenantIdRegistry, IDisposable
{
    private readonly ITenantStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InMemoryTenantIdRegistry(ITenantStore store) => _store = store;

    public async ValueTask<TenantCreateResult> TryCreateAsync(Tenant tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await _store.GetAsync(tenant.TenantId, ct).ConfigureAwait(false) is not null)
                return TenantCreateResult.Conflict;
            foreach (var stored in await SnapshotAsync(ct).ConfigureAwait(false))
            {
                if (TenantIdRule.Overlaps(stored.TenantId, tenant.TenantId))
                    return TenantCreateResult.Conflict;
            }
            await _store.UpsertAsync(tenant, ct).ConfigureAwait(false);
            return TenantCreateResult.Created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<StoredTenantId>> ListAllIdsAsync(CancellationToken ct) =>
        [.. (await SnapshotAsync(ct).ConfigureAwait(false)).Select(t => new StoredTenantId(t.TenantId, t.Status))];

    public void Dispose() => _gate.Dispose();

    private async ValueTask<IReadOnlyList<Tenant>> SnapshotAsync(CancellationToken ct) =>
        _store is InMemoryTenantStore inMemory ? inMemory.Snapshot() : await _store.GetAllActiveAsync(ct).ConfigureAwait(false) ?? [];
}
