using Verbara.Platform.Audit;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// Dev/test mirror of <c>PostgresTypificationCorrectionAuditWriter</c>
/// (audit-trail-integrity-fixes, fix 1). The InMemory stores have no crash-recovery concept — a
/// single in-process write cannot partially fail — so "atomic" here means a lock-scoped compound
/// operation: the three underlying writes happen while holding one lock, so no concurrent reader
/// can observe the correction/submission/audit trio in a partial state (design.md: "InMemory
/// mirror = lock-scoped compound").
/// </summary>
internal sealed class InMemoryTypificationCorrectionAuditWriter : ITypificationCorrectionAuditWriter
{
    private readonly ITypificationSubmissionCorrectionStore _correctionStore;
    private readonly ITypificationSubmissionStore _submissionStore;
    private readonly IAuditStore _auditStore;
    private readonly Lock _gate = new();

    public InMemoryTypificationCorrectionAuditWriter(
        ITypificationSubmissionCorrectionStore correctionStore,
        ITypificationSubmissionStore submissionStore,
        IAuditStore auditStore)
    {
        _correctionStore = correctionStore;
        _submissionStore = submissionStore;
        _auditStore = auditStore;
    }

    public Task CommitAsync(
        TypificationSubmissionCorrection correction,
        TypificationSubmission submission,
        AuditEntry auditEntry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(auditEntry);

        // The underlying InMemory stores complete synchronously (ConcurrentDictionary-backed,
        // returning Task.CompletedTask) — the three calls are made and DRAINED (GetAwaiter().
        // GetResult()) entirely INSIDE the lock's scope. A System.Threading.Lock.Scope is a ref
        // struct and cannot legally span an `await` in an async method, so this method stays
        // synchronous instead of `async`/`await`ing while holding the scope.
        using (_gate.EnterScope())
        {
            _correctionStore.InsertAsync(correction, ct).GetAwaiter().GetResult();
            _submissionStore.SaveAsync(submission, ct).GetAwaiter().GetResult();
            _auditStore.SaveAsync(auditEntry, ct).GetAwaiter().GetResult();
        }

        return Task.CompletedTask;
    }
}
