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
    /// Returns the number of audit-trail rows attributed to the given actor within the tenant
    /// (<see cref="AuditEntry.ActorId"/> match) — the real count backing
    /// <c>GdprPurgeService.PreviewUserPurgeAsync</c>'s <c>AuditTrailCount</c> (audit-trail-integrity-fixes,
    /// fix 2). Not zero-padded or estimated: an exact <c>COUNT(*)</c>.
    /// </summary>
    Task<long> CountByActorAsync(TenantId tenantId, string actorId, CancellationToken ct);

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

    /// <summary>
    /// Deletes audit entries older than <paramref name="cutoff"/> and returns the count deleted
    /// (retention policy). Implementations MUST honour the per-record retention floor
    /// (<see cref="AuditEntry.RetainUntil"/>, ADR-0034 Decision 4): an entry is deleted only when it is
    /// both older than the cutoff AND its retention floor is null or already elapsed (compared to the
    /// CURRENT time, not the cutoff — the cutoff is in the past). A record still inside its window
    /// (e.g. an autonomous-disposition decision within the correction window) is preserved.
    /// </summary>
    Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct);

    /// <summary>
    /// GDPR Art. 17 redaction (ADR-0034 Decision 4): for the supplied conversation IDs, redacts the
    /// contact-identifying linkage on autonomous-disposition AI-actor audit records
    /// (<c>action = "typification.autonomous.commit"</c>) — nulling <see cref="AuditEntry.TargetId"/>
    /// and the <c>conversation</c> metadata key — while RETAINING the decision-fact (node path, leaf
    /// node, confidence, timestamp, AI actor) under the Art. 17(3) legal-defence exemption. The record
    /// is NOT deleted. The integrity hash is recomputed over the redacted canonical fields and a
    /// <c>redacted</c> marker is stamped so the record stays tamper-evident in its redacted state.
    /// Returns the number of audit records redacted.
    /// </summary>
    Task<int> RedactContactLinkageAsync(
        TenantId tenantId, IReadOnlyCollection<string> conversationIds, CancellationToken ct);
}
