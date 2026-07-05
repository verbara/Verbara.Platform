using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Typification;
using FluentAssertions;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Live-DB coverage for <see cref="PostgresTypificationCorrectionAuditWriter"/>
/// (audit-trail-integrity-fixes, fix 1): the correction insert, submission UPSERT, and audit
/// insert commit as ONE Postgres transaction — a conflict on ANY of the three writes rolls ALL
/// of them back, so a fault can never leave a correction with no audit trail (or vice versa).
/// </summary>
public sealed class PostgresTypificationCorrectionAuditWriterTests
    : IClassFixture<TypificationCorrectionAuditFixture>, IAsyncLifetime
{
    private readonly TypificationCorrectionAuditFixture _fixture;
    private readonly PostgresTypificationCorrectionAuditWriter _writer;
    private readonly PostgresTypificationSubmissionCorrectionStore _correctionStore;
    private readonly PostgresTypificationSubmissionStore _submissionStore;
    private readonly PostgresAuditStore _auditStore;
    private static readonly TenantId Tenant = new("tenant-correction-audit");

    public PostgresTypificationCorrectionAuditWriterTests(TypificationCorrectionAuditFixture fixture)
    {
        _fixture = fixture;
        _writer = new PostgresTypificationCorrectionAuditWriter(_fixture.DataSource);
        _correctionStore = new PostgresTypificationSubmissionCorrectionStore(_fixture.DataSource);
        _submissionStore = new PostgresTypificationSubmissionStore(_fixture.DataSource);
        _auditStore = new PostgresAuditStore(_fixture.DataSource);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static TypificationSubmission NewAutoAiSubmission(EntityId conversationId, EntityId leafNodeId) => new()
    {
        TenantId = Tenant,
        ConversationId = conversationId,
        AgentId = EntityId.From("agent-1"),
        SchemaId = EntityId.From("schema-1"),
        SchemaVersion = 1,
        SelectedNodePath = [EntityId.From("root"), leafNodeId],
        LeafNodeId = leafNodeId,
        FieldValues = new Dictionary<string, string>(),
        Source = SubmissionSource.AutoAi,
        AutonomousActorId = "verbara:ai:autonomous-worker",
        CompletedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        Duration = TimeSpan.Zero,
    };

    [Fact]
    public async Task CommitAsync_ShouldPersistAllThreeWrites_WhenCalled()
    {
        var conversationId = EntityId.From("conv-atomic-1");
        var originalLeaf = EntityId.From("leaf-original");
        var correctedLeaf = EntityId.From("leaf-corrected");

        // Seed the original AutoAi submission (uncorrected) — mirrors what the endpoint reads
        // before applying guards.
        await _submissionStore.SaveAsync(NewAutoAiSubmission(conversationId, originalLeaf), CancellationToken.None);

        var now = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);
        var correction = new TypificationSubmissionCorrection
        {
            TenantId = Tenant,
            ConversationId = conversationId,
            CorrectedLeafNodeId = correctedLeaf,
            CorrectedNodePath = [EntityId.From("root"), correctedLeaf],
            CorrectedByUserId = "supervisor-1",
            CorrectedAt = now,
        };
        var correctedSubmission = NewAutoAiSubmission(conversationId, originalLeaf) with
        {
            CorrectionState = CorrectionState.Corrected,
            CorrectedAt = now,
        };
        var metadata = new Dictionary<string, string>
        {
            ["original_node_path"] = "root > leaf-original",
            ["corrected_node_path"] = "root > leaf-corrected",
            ["confirmed"] = "false",
        };
        var auditEntry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant,
            Action = "typification.autonomous.corrected",
            Category = "conversations",
            Severity = "info",
            ActorId = "supervisor-1",
            ActorType = "user",
            TargetId = conversationId.Value,
            TargetType = "Conversation",
            Metadata = metadata,
            OccurredAt = now,
            RetainUntil = now.AddDays(90),
            IntegrityHash = DefaultAuditService.ComputeIntegrityHashV2(
                Tenant, "user", "supervisor-1", "typification.autonomous.corrected",
                "Conversation", conversationId.Value, now, now.AddDays(90), metadata),
        };

        await _writer.CommitAsync(correction, correctedSubmission, auditEntry, CancellationToken.None);

        // (a) The correction record exists.
        var storedCorrection = await _correctionStore.GetAsync(Tenant, conversationId, CancellationToken.None);
        storedCorrection.Should().NotBeNull();
        storedCorrection!.CorrectedLeafNodeId.Should().Be(correctedLeaf);
        storedCorrection.CorrectedByUserId.Should().Be("supervisor-1");

        // (b) The submission's status pointers are flipped — the AI fields stay byte-identical.
        var storedSubmission = await _submissionStore.GetByConversationIdAsync(Tenant, conversationId, CancellationToken.None);
        storedSubmission.Should().NotBeNull();
        storedSubmission!.CorrectionState.Should().Be(CorrectionState.Corrected);
        storedSubmission.CorrectedAt.Should().Be(now);
        storedSubmission.LeafNodeId.Should().Be(originalLeaf, "the original AI decision stays immutable");
        storedSubmission.Source.Should().Be(SubmissionSource.AutoAi);

        // (c) The audit entry exists and verifies under the v2 scheme.
        var auditRows = await _auditStore.GetByEntityAsync(Tenant, "Conversation", conversationId.Value, CancellationToken.None);
        auditRows.Should().ContainSingle();
        var storedAudit = auditRows[0];
        storedAudit.Action.Should().Be("typification.autonomous.corrected");
        storedAudit.ActorId.Should().Be("supervisor-1");
        storedAudit.RetainUntil.Should().Be(now.AddDays(90));
        DefaultAuditService.VerifyIntegrity(storedAudit).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_ShouldRollBackAllThreeWrites_WhenCorrectionConflicts()
    {
        // A correction already exists for this conversation (simulating the AlreadyCorrected race
        // the endpoint's guard normally prevents pre-write) — the correction INSERT hits the
        // (tenant_id, conversation_id) primary-key conflict, and the WHOLE transaction — including
        // the submission UPSERT and the audit INSERT — must roll back. No orphan audit record and
        // no orphan status-pointer flip.
        var conversationId = EntityId.From("conv-conflict-1");
        var leaf = EntityId.From("leaf-1");

        var firstCorrection = new TypificationSubmissionCorrection
        {
            TenantId = Tenant,
            ConversationId = conversationId,
            CorrectedLeafNodeId = leaf,
            CorrectedNodePath = [leaf],
            CorrectedByUserId = "supervisor-1",
            CorrectedAt = DateTimeOffset.UtcNow,
        };
        await _correctionStore.InsertAsync(firstCorrection, CancellationToken.None);

        var conflictingCorrection = new TypificationSubmissionCorrection
        {
            TenantId = firstCorrection.TenantId,
            ConversationId = firstCorrection.ConversationId,
            CorrectedLeafNodeId = firstCorrection.CorrectedLeafNodeId,
            CorrectedNodePath = firstCorrection.CorrectedNodePath,
            CorrectedByUserId = "supervisor-2",
            CorrectedAt = firstCorrection.CorrectedAt,
        };
        var submission = NewAutoAiSubmission(conversationId, leaf) with
        {
            CorrectionState = CorrectionState.Corrected,
            CorrectedAt = DateTimeOffset.UtcNow,
        };
        var metadata = new Dictionary<string, string> { ["test"] = "conflict" };
        var now = DateTimeOffset.UtcNow;
        var auditEntry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant,
            Action = "typification.autonomous.corrected",
            Category = "conversations",
            Severity = "info",
            ActorId = "supervisor-2",
            ActorType = "user",
            TargetId = conversationId.Value,
            TargetType = "Conversation",
            Metadata = metadata,
            OccurredAt = now,
            RetainUntil = now.AddDays(90),
            IntegrityHash = DefaultAuditService.ComputeIntegrityHashV2(
                Tenant, "user", "supervisor-2", "typification.autonomous.corrected",
                "Conversation", conversationId.Value, now, now.AddDays(90), metadata),
        };

        var act = async () => await _writer.CommitAsync(conflictingCorrection, submission, auditEntry, CancellationToken.None);

        await act.Should().ThrowAsync<PostgresException>()
            .Where(ex => ex.SqlState == "23505"); // unique_violation (primary key)

        // Nothing from the failed attempt was committed: the submission was never saved (it did
        // not exist before this test's SUT call), and no audit row exists for the action.
        var storedSubmission = await _submissionStore.GetByConversationIdAsync(Tenant, conversationId, CancellationToken.None);
        storedSubmission.Should().BeNull("the submission UPSERT must roll back with the rest of the transaction");

        var auditRows = await _auditStore.GetByEntityAsync(Tenant, "Conversation", conversationId.Value, CancellationToken.None);
        auditRows.Should().BeEmpty("the audit insert must roll back — no orphan audit record from a failed correction");

        // Only the ORIGINAL correction (inserted before the SUT call) survives.
        var storedCorrection = await _correctionStore.GetAsync(Tenant, conversationId, CancellationToken.None);
        storedCorrection.Should().NotBeNull();
        storedCorrection!.CorrectedByUserId.Should().Be("supervisor-1", "the conflicting second write never committed");
    }
}
