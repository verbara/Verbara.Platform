using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Stores;

/// <summary>
/// Persistence abstraction for the per-tenant autonomous-disposition activation gate (ADR-0034).
/// The gate is a controller-instruction config record, never the data subject's consent.
/// Implementations: <c>InMemoryTenantAutonomousDispositionStore</c> (dev/test),
/// <c>PostgresTenantAutonomousDispositionStore</c> (production).
/// </summary>
public interface ITenantAutonomousDispositionStore
{
    /// <summary>
    /// Returns the tenant's activation record (whether active or revoked), or <see langword="null"/>
    /// if the tenant has never recorded one (a valid "autonomous stamping off" state).
    /// </summary>
    Task<TenantAutonomousDisposition?> GetAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>
    /// Records (or re-records, clearing any prior revocation) the tenant's activation instruction.
    /// One row per tenant.
    /// </summary>
    Task UpsertAsync(TenantAutonomousDisposition record, CancellationToken ct);

    /// <summary>
    /// Soft-deletes the tenant's activation: sets <c>revoked_at</c> (now, UTC) and
    /// <c>revoked_by_user_id</c>. A no-op if no row exists. Subsequent close cycles SHALL NOT
    /// stamp dispositions for the tenant.
    /// </summary>
    Task RevokeAsync(TenantId tenantId, EntityId revokedBy, CancellationToken ct);
}
