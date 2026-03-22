using Asterisk.Platform.Core;

namespace Asterisk.Platform.Audit;

/// <summary>
/// Persistence contract for audit entries. Implementations must be append-only.
/// </summary>
public interface IAuditStore
{
    /// <summary>Persists a new audit entry.</summary>
    Task SaveAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>
    /// Returns all audit entries for a specific entity, ordered by <see cref="AuditEntry.OccurredAt"/> ascending.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> GetByEntityAsync(TenantId tenantId, string entityType, string entityId, CancellationToken ct);

    /// <summary>Returns a paged set of audit entries matching the supplied query.</summary>
    Task<PagedResult<AuditEntry>> SearchAsync(TenantId tenantId, AuditQuery query, CancellationToken ct);
}
