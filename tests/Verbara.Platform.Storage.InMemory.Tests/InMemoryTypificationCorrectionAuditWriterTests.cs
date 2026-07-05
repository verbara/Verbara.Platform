using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// Unit coverage for <see cref="InMemoryTypificationCorrectionAuditWriter"/>
/// (audit-trail-integrity-fixes, fix 1) — the lock-scoped compound mirror of the atomic Postgres
/// transaction. All three writes land together, and a conflict on the FIRST write (the correction
/// insert, which enforces "one correction per conversation" via its dictionary key) leaves no
/// partial state — the submission and audit stores are never touched.
/// </summary>
public sealed class InMemoryTypificationCorrectionAuditWriterTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId Conversation = EntityId.From("conv-1");

    private static TypificationSubmission NewSubmission(EntityId leaf) => new()
    {
        TenantId = Tenant,
        ConversationId = Conversation,
        AgentId = EntityId.From("agent-1"),
        SchemaId = EntityId.From("schema-1"),
        SchemaVersion = 1,
        SelectedNodePath = [EntityId.From("root"), leaf],
        LeafNodeId = leaf,
        FieldValues = new Dictionary<string, string>(),
        Source = SubmissionSource.AutoAi,
        CompletedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static TypificationSubmissionCorrection NewCorrection(EntityId leaf, string byUser = "supervisor-1") => new()
    {
        TenantId = Tenant,
        ConversationId = Conversation,
        CorrectedLeafNodeId = leaf,
        CorrectedNodePath = [EntityId.From("root"), leaf],
        CorrectedByUserId = byUser,
        CorrectedAt = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
    };

    private static AuditEntry NewAuditEntry(string actorId = "supervisor-1")
    {
        var occurredAt = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);
        var retainUntil = occurredAt.AddDays(90);
        var metadata = new Dictionary<string, string> { ["test"] = "value" };
        return new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant,
            Action = "typification.autonomous.corrected",
            Category = "conversations",
            Severity = "info",
            ActorId = actorId,
            ActorType = "user",
            TargetId = Conversation.Value,
            TargetType = "Conversation",
            Metadata = metadata,
            OccurredAt = occurredAt,
            RetainUntil = retainUntil,
            IntegrityHash = DefaultAuditService.ComputeIntegrityHashV2(
                Tenant, "user", actorId, "typification.autonomous.corrected",
                "Conversation", Conversation.Value, occurredAt, retainUntil, metadata),
        };
    }

    [Fact]
    public async Task CommitAsync_ShouldPersistAllThreeWrites_WhenCalled()
    {
        var correctionStore = new InMemoryTypificationSubmissionCorrectionStore();
        var submissionStore = new InMemoryTypificationSubmissionStore();
        var auditStore = new InMemoryAuditStore();
        var writer = new InMemoryTypificationCorrectionAuditWriter(correctionStore, submissionStore, auditStore);

        var originalLeaf = EntityId.From("leaf-original");
        var correctedLeaf = EntityId.From("leaf-corrected");
        var correction = NewCorrection(correctedLeaf);
        var submission = NewSubmission(originalLeaf) with
        {
            CorrectionState = CorrectionState.Corrected,
            CorrectedAt = correction.CorrectedAt,
        };
        var auditEntry = NewAuditEntry();

        await writer.CommitAsync(correction, submission, auditEntry, CancellationToken.None);

        var storedCorrection = await correctionStore.GetAsync(Tenant, Conversation, CancellationToken.None);
        storedCorrection.Should().NotBeNull();
        storedCorrection!.CorrectedLeafNodeId.Should().Be(correctedLeaf);

        var storedSubmission = await submissionStore.GetByConversationIdAsync(Tenant, Conversation, CancellationToken.None);
        storedSubmission.Should().NotBeNull();
        storedSubmission!.CorrectionState.Should().Be(CorrectionState.Corrected);
        storedSubmission.LeafNodeId.Should().Be(originalLeaf, "the original AI decision stays immutable");

        var auditRows = await auditStore.GetByEntityAsync(Tenant, "Conversation", Conversation.Value, CancellationToken.None);
        auditRows.Should().ContainSingle();
        DefaultAuditService.VerifyIntegrity(auditRows[0]).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_ShouldLeaveNoPartialState_WhenCorrectionAlreadyExists()
    {
        var correctionStore = new InMemoryTypificationSubmissionCorrectionStore();
        var submissionStore = new InMemoryTypificationSubmissionStore();
        var auditStore = new InMemoryAuditStore();
        var writer = new InMemoryTypificationCorrectionAuditWriter(correctionStore, submissionStore, auditStore);

        var firstLeaf = EntityId.From("leaf-first");
        await correctionStore.InsertAsync(NewCorrection(firstLeaf), CancellationToken.None);

        // A second CommitAsync for the SAME conversation must fail on the correction insert
        // (mirrors the Postgres primary-key conflict) BEFORE touching the submission or audit
        // stores — no orphan submission flip, no orphan audit record.
        var conflictingLeaf = EntityId.From("leaf-conflict");
        var conflictingCorrection = NewCorrection(conflictingLeaf, byUser: "supervisor-2");
        var submission = NewSubmission(firstLeaf) with { CorrectionState = CorrectionState.Corrected };
        var auditEntry = NewAuditEntry(actorId: "supervisor-2");

        var act = async () => await writer.CommitAsync(conflictingCorrection, submission, auditEntry, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        var storedSubmission = await submissionStore.GetByConversationIdAsync(Tenant, Conversation, CancellationToken.None);
        storedSubmission.Should().BeNull("the submission write must not happen when the correction insert fails");

        var auditRows = await auditStore.GetByEntityAsync(Tenant, "Conversation", Conversation.Value, CancellationToken.None);
        auditRows.Should().BeEmpty("the audit write must not happen when the correction insert fails");

        var storedCorrection = await correctionStore.GetAsync(Tenant, Conversation, CancellationToken.None);
        storedCorrection!.CorrectedByUserId.Should().Be("supervisor-1", "the original correction is untouched");
    }
}
