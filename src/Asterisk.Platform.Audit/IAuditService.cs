using Asterisk.Platform.Core;

namespace Asterisk.Platform.Audit;

/// <summary>
/// High-level service for recording auditable actions.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Records an auditable action, creating and persisting an <see cref="AuditEntry"/>.
    /// </summary>
    Task LogAsync(
        TenantId tenantId,
        string action,
        string entityType,
        string entityId,
        string? performedBy = null,
        IReadOnlyDictionary<string, string>? details = null,
        CancellationToken ct = default);
}
