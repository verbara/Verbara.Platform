using Verbara.Platform.Core;

namespace Verbara.Platform.Routing.Inbound;

/// <summary>
/// Persistence contract for <see cref="DidRoute"/> (inbound DID → queue mapping).
/// All reads are tenant-scoped. <see cref="CreateAsync"/> rejects a duplicate
/// <c>(tenant_id, did)</c> with an <see cref="InvalidOperationException"/> on
/// both the Postgres and in-memory backends so callers behave identically.
/// </summary>
public interface IDidRouteStore
{
    Task<IReadOnlyList<DidRoute>> ListAsync(TenantId tenantId, CancellationToken ct);
    Task<IReadOnlyList<DidRoute>> ListActiveAsync(TenantId tenantId, CancellationToken ct);
    Task<DidRoute?> GetAsync(TenantId tenantId, EntityId routeId, CancellationToken ct);
    Task<DidRoute?> GetByDidAsync(TenantId tenantId, string did, CancellationToken ct);
    Task<DidRoute> CreateAsync(DidRoute route, CancellationToken ct);
    Task UpdateAsync(DidRoute route, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId routeId, CancellationToken ct);
}
