using Asterisk.Platform.Core;

namespace Asterisk.Platform.Audit;

/// <summary>
/// High-level service for recording auditable actions.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Records an auditable action using the full structured model.
    /// </summary>
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
