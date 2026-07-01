using Verbara.Platform.Core;

namespace Verbara.Platform.Typification;

/// <summary>
/// Per-tenant activation gate for autonomous AI disposition stamping (ADR-0034 Decision 3).
/// This is a documented <b>controller instruction</b> + configuration gate under the
/// data-processing agreement — it is NOT, and SHALL NOT be represented as, the data subject's
/// consent. A present, non-revoked row enables autonomous stamping for the tenant; revocation
/// (soft-delete via <see cref="RevokedAt"/>) disables it for subsequent close cycles. One row per
/// tenant (primary key <c>tenant_id</c>), persisted in <c>tenant_autonomous_disposition</c>.
/// </summary>
public sealed class TenantAutonomousDisposition
{
    /// <summary>Tenant that owns this activation gate (primary key).</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>User ID of the privileged admin who recorded the activation instruction.</summary>
    public required string AttestedByUserId { get; init; }

    /// <summary>UTC timestamp when the activation instruction was recorded.</summary>
    public required DateTimeOffset AttestedAt { get; init; }

    /// <summary>UTC timestamp when the gate was revoked (null while active).</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>User ID of the admin who revoked the gate (null while active).</summary>
    public string? RevokedByUserId { get; init; }

    /// <summary>Whether the gate is currently active (recorded and not revoked).</summary>
    public bool IsActive => RevokedAt is null;
}
