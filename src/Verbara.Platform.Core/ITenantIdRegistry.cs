using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Core;

/// <summary>The outcome of <see cref="ITenantIdRegistry.TryCreateAsync"/>.</summary>
public enum TenantCreateResult
{
    /// <summary>No stored tenant overlapped the id; the tenant was inserted.</summary>
    Created,

    /// <summary>A stored tenant, of any status, overlaps the id (<see cref="TenantIdRule.Overlaps"/>); nothing was written.</summary>
    Conflict,
}

/// <summary>A stored tenant id with its status.</summary>
public sealed record StoredTenantId(string TenantId, TenantStatus Status);

/// <summary>
/// The single path that creates a tenant with a new id (realtime-objects-stay-in-their-tenant, design D1): the overlap
/// check and the insert are atomic, so two concurrent creates of overlapping ids leave at most one. Updates of an
/// existing tenant still go through <see cref="ITenantStore.UpsertAsync"/>.
/// </summary>
public interface ITenantIdRegistry
{
    /// <summary>
    /// Inserts <paramref name="tenant"/> unless a stored tenant, of any status, overlaps its id; never overwrites. The
    /// invariants <see cref="ITenantStore.UpsertAsync"/> enforces (one Platform tenant, depth 3) still apply and throw.
    /// </summary>
    ValueTask<TenantCreateResult> TryCreateAsync(Tenant tenant, CancellationToken ct);

    /// <summary>Every stored tenant id, of any status (<see cref="TenantStatus.Deleted"/> included).</summary>
    ValueTask<IReadOnlyList<StoredTenantId>> ListAllIdsAsync(CancellationToken ct);
}
