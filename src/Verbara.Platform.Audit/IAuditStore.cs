using Verbara.Platform.Core;

namespace Verbara.Platform.Audit;

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

    /// <summary>
    /// Streams entries matching the supplied query in batches without buffering
    /// the full result set. Used by R5.2 PB.1 export endpoint to drain large
    /// result sets to CSV / JSON without loading them into memory.
    /// </summary>
    /// <remarks>
    /// Pagination fields on the query are ignored — implementations stream all
    /// matches in <c>occurred_at DESC</c> order. Implementations should batch
    /// internally (default 500 rows per round-trip is fine).
    /// </remarks>
    IAsyncEnumerable<AuditEntry> StreamAsync(TenantId tenantId, AuditQuery query, CancellationToken ct);

    /// <summary>Deletes audit entries older than cutoff and returns the count deleted (retention policy).</summary>
    Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct);
}
