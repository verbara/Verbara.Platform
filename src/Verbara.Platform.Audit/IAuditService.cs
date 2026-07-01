using Verbara.Platform.Core;

namespace Verbara.Platform.Audit;

/// <summary>
/// High-level service for recording auditable actions.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Records an auditable action using the full structured model.
    /// </summary>
    /// <remarks>
    /// The optional <c>retainUntil</c> sets a per-record retention floor (ADR-0034 Decision 4): when
    /// set, the blanket time-based purge in <c>AuditRetentionService</c> MUST NOT delete this record
    /// before that UTC instant — e.g. an autonomous-disposition AI-actor event carries
    /// <c>now + correction window</c> so the sweep cannot destroy a decision still inside its
    /// correction window. Null (the default) leaves the record subject to the normal blanket purge.
    /// </remarks>
    Task RecordAsync(
        TenantId tenantId,
        string category,
        string action,
        string severity,
        string actorId,
        string actorType,
        string? targetId = null,
        string? targetType = null,
        Guid? correlationId = null,
        AuditChanges? changes = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        DateTimeOffset? retainUntil = null,
        CancellationToken ct = default);

    /// <summary>
    /// Records an auditable action using the legacy field model.
    /// Category is inferred from the action name prefix.
    /// </summary>
    [Obsolete("Use RecordAsync for full structured audit entries.")]
    Task LogAsync(
        TenantId tenantId,
        string action,
        string entityType,
        string entityId,
        string? performedBy = null,
        IReadOnlyDictionary<string, string>? details = null,
        CancellationToken ct = default);
}
