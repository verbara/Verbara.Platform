using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Stores;

/// <summary>
/// Persistence abstraction for the append-only operator corrections of autonomously stamped
/// dispositions (ADR-0034 Decision 5). A correction is a SEPARATE record — it never mutates the
/// immutable original <see cref="TypificationSubmission"/>. One row per conversation.
/// Implementations: <c>InMemoryTypificationSubmissionCorrectionStore</c> (dev/test),
/// <c>PostgresTypificationSubmissionCorrectionStore</c> (production).
/// </summary>
public interface ITypificationSubmissionCorrectionStore
{
    /// <summary>
    /// Returns the correction record for a conversation, or <see langword="null"/> if the
    /// conversation has never been corrected.
    /// </summary>
    Task<TypificationSubmissionCorrection?> GetAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);

    /// <summary>
    /// Inserts a new correction record (append-only). A conversation is correctable at most once;
    /// the store's primary key <c>(tenant_id, conversation_id)</c> enforces that.
    /// </summary>
    Task InsertAsync(TypificationSubmissionCorrection record, CancellationToken ct);
}
