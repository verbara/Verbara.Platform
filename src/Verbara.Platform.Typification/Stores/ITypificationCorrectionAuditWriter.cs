using Verbara.Platform.Audit;
using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Stores;

/// <summary>
/// Atomically persists a supervisor correction of an autonomously stamped disposition together
/// with its audit record (audit-trail-integrity-fixes, fix 1). Closes the correction endpoint's
/// 2-write window (<c>ConversationEndpoints.CorrectTypification</c> previously called
/// <see cref="ITypificationSubmissionCorrectionStore.InsertAsync"/>,
/// <see cref="ITypificationSubmissionStore.SaveAsync"/>, and <see cref="IAuditService.RecordAsync"/>
/// as three separate, non-atomic writes): either the correction record, the submission's flipped
/// status pointers, AND the audit record are ALL durable, or NONE of them are — a fault between
/// writes can never leave a correction with no audit trail (or an audit trail with no correction).
/// </summary>
/// <remarks>
/// Implementations: <c>PostgresTypificationCorrectionAuditWriter</c> (a single Postgres
/// transaction spanning all three writes — the correction and submission stores already sit on
/// <c>Verbara.Sdk.Data.Npgsql</c>, so they share a connection/transaction seam); the InMemory
/// mirror (dev/test) scopes the three in-memory mutations under one lock so a concurrent reader
/// never observes a partial write.
/// </remarks>
public interface ITypificationCorrectionAuditWriter
{
    /// <summary>
    /// Inserts <paramref name="correction"/>, saves the corrected <paramref name="submission"/>
    /// (status pointers flipped, AI fields byte-identical), and persists
    /// <paramref name="auditEntry"/> — all as a single atomic unit.
    /// </summary>
    Task CommitAsync(
        TypificationSubmissionCorrection correction,
        TypificationSubmission submission,
        AuditEntry auditEntry,
        CancellationToken ct);
}
