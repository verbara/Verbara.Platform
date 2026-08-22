using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Close-path enrichment tests for autonomous AI disposition stamping (ADR-0034, task group 4).
/// These exercise <see cref="ConversationTimeoutWorker.ProcessTimeoutsAsync"/> directly (the C3
/// determinism idiom — never <c>FakeTimeProvider.Advance</c> on the loop), feeding a single
/// timed-out wrap-up per case and asserting the commit / skip / CAS-race / cap behaviour.
/// </summary>
public sealed class ConversationTimeoutWorkerAutonomousTests
{
    private const string TestTenantId = "t001";
    private const string AutonomousActorId = "verbara:ai:autonomous-worker";

    private readonly IConversationStore _conversationStore = Substitute.For<IConversationStore>();
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private readonly IConversationSwitchboard _switchboard = Substitute.For<IConversationSwitchboard>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAutonomousDispositionPolicy _policy = Substitute.For<IAutonomousDispositionPolicy>();
    private readonly IAiSuggestionStore _suggestionStore = Substitute.For<IAiSuggestionStore>();
    private readonly ITypificationSubmissionStore _submissionStore = Substitute.For<ITypificationSubmissionStore>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();

    private readonly DateTimeOffset _now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    public ConversationTimeoutWorkerAutonomousTests()
    {
        _clock.UtcNow.Returns(_now);
        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { MakeActiveTenant(TestTenantId) });

        // Only the WrapUp state list is populated per-test; offer/queue are empty.
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Offered, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Queued, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Tenant MakeActiveTenant(string tenantId) => new()
    {
        TenantId = tenantId,
        Name = $"Tenant {tenantId}",
        Status = TenantStatus.Active,
    };

    private Conversation MakeExpiredWrapUp() => new()
    {
        ConversationId = EntityId.New(),
        TenantId = new TenantId(TestTenantId),
        ContactId = EntityId.New(),
        Channel = ChannelType.WhatsApp,
        Owner = ConversationOwner.ForAgent(EntityId.From("a1")),
        State = ConversationState.WrapUp,
        CreatedAt = _now.AddMinutes(-10),
        // UpdatedAt 200s ago, timeout is 120s → expired.
        UpdatedAt = _now.AddSeconds(-200),
    };

    private AiSuggestionRecord MakeSuggestion(EntityId conversationId, double confidence = 0.97) => new()
    {
        Id = EntityId.New(),
        TenantId = EntityId.From(TestTenantId),
        ConversationId = conversationId,
        SchemaId = EntityId.From("schema-1"),
        SchemaVersion = 3,
        SuggestedLeafNodeId = EntityId.From("leaf-9"),
        SuggestedNodePath = ["root", "sales", "completed"],
        SuggestedFieldValues = new Dictionary<string, string> { ["disp"] = "won" },
        Confidence = confidence,
        ModelId = "gpt-test",
        PromptVersion = "v1",
        SurfacedBand = TypificationBand.None,
        CreatedAt = _now.AddSeconds(-210),
    };

    private ConversationTimeoutWorker CreateWorker(
        bool autonomousEnabled, int perCycleCap = 50, int correctionWindowDays = 30,
        ITenantAutonomousDispositionStore? gateStore = null)
    {
        var options = Options.Create(new DistributionOptions
        {
            AutonomousDispositionEnabled = autonomousEnabled,
            AutonomousDispositionPerCycleCap = perCycleCap,
            AutonomousCorrectionWindowDays = correctionWindowDays,
        });

        return new ConversationTimeoutWorker(
            _conversationStore,
            _tenantStore,
            _switchboard,
            new PlatformEventBus(),
            _clock,
            new Verbara.Platform.Api.Health.ServiceHeartbeat(),
            options,
            NullLogger<ConversationTimeoutWorker>.Instance,
            policy: null,
            autonomousPolicy: _policy,
            suggestionStore: _suggestionStore,
            submissionStore: _submissionStore,
            auditService: _audit,
            autonomousMetrics: new AutonomousDispositionMetrics(),
            gateStore: gateStore);
    }

    private void SeedWrapUp(params Conversation[] convs) =>
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.WrapUp, 100, Arg.Any<CancellationToken>())
            .Returns(convs);

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldCloseWithoutSubmission_WhenGateOff()
    {
        // (a) Global breaker OFF → byte-identical: blank close, NO submission, NO policy lookup.
        var conv = MakeExpiredWrapUp();
        SeedWrapUp(conv);
        var sut = CreateWorker(autonomousEnabled: false);

        await sut.ProcessTimeoutsAsync(CancellationToken.None);

        conv.State.Should().Be(ConversationState.Closed);
        conv.ClosedAt.Should().Be(_now);
        await _conversationStore.Received(1).SaveAsync(conv, Arg.Any<CancellationToken>());
        await _submissionStore.DidNotReceive().SaveAsync(Arg.Any<TypificationSubmission>(), Arg.Any<CancellationToken>());
        await _policy.DidNotReceiveWithAnyArgs().EvaluateAsync(default!, default, default);
        await _conversationStore.DidNotReceive().TryConditionalCloseWrapUpAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldStampAutoAiAndAudit_WhenCommitWinsCas()
    {
        // (b) breaker ON + commit decision + CAS win → AutoAi submission + AutonomousCommit audit with RetainUntil.
        var conv = MakeExpiredWrapUp();
        var suggestion = MakeSuggestion(conv.ConversationId);
        SeedWrapUp(conv);

        _suggestionStore.GetLatestForConversationAsync(
                Arg.Any<EntityId>(), conv.ConversationId, Arg.Any<CancellationToken>())
            .Returns(suggestion);
        _policy.EvaluateAsync(conv, suggestion, Arg.Any<CancellationToken>())
            .Returns(AutonomousDispositionDecision.Commit(
                EntityId.From("leaf-9"), ["root", "sales", "completed"], 0.97));
        _conversationStore.TryConditionalCloseWrapUpAsync(
                Arg.Any<TenantId>(), conv.ConversationId, _now, Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = CreateWorker(autonomousEnabled: true, correctionWindowDays: 30);

        await sut.ProcessTimeoutsAsync(CancellationToken.None);

        // No blank SaveAsync (the CAS path closes via the conditional update, not SaveAsync).
        await _conversationStore.DidNotReceive().SaveAsync(conv, Arg.Any<CancellationToken>());

        await _submissionStore.Received(1).SaveAsync(
            Arg.Is<TypificationSubmission>(s => s != null &&
                s.Source == SubmissionSource.AutoAi
                && s.AutonomousActorId == AutonomousActorId
                && s.AgentId.Value == AutonomousActorId
                && s.LeafNodeId.Value == "leaf-9"
                && s.AiConfidence == 0.97
                && s.CompletedAt == _now
                && s.SchemaId.Value == "schema-1"
                && s.SchemaVersion == 3),
            Arg.Any<CancellationToken>());

        await _audit.Received(1).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == TestTenantId),
            "conversations",
            "typification.autonomous.commit",
            "info",
            AutonomousActorId,
            "ai",
            conv.ConversationId.Value,
            "Conversation",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Is<DateTimeOffset?>(r => r == _now.AddDays(30)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldNotStamp_WhenConcurrentHumanWinsCas()
    {
        // (c) breaker ON + commit decision but the conditional close affects 0 rows (human typified
        // concurrently) → NO submission, NO commit audit, NO blank close (the human owns the close).
        var conv = MakeExpiredWrapUp();
        var suggestion = MakeSuggestion(conv.ConversationId);
        SeedWrapUp(conv);

        _suggestionStore.GetLatestForConversationAsync(
                Arg.Any<EntityId>(), conv.ConversationId, Arg.Any<CancellationToken>())
            .Returns(suggestion);
        _policy.EvaluateAsync(conv, suggestion, Arg.Any<CancellationToken>())
            .Returns(AutonomousDispositionDecision.Commit(
                EntityId.From("leaf-9"), ["root", "sales", "completed"], 0.97));
        _conversationStore.TryConditionalCloseWrapUpAsync(
                Arg.Any<TenantId>(), conv.ConversationId, _now, Arg.Any<CancellationToken>())
            .Returns(false); // human won the race

        var sut = CreateWorker(autonomousEnabled: true);

        await sut.ProcessTimeoutsAsync(CancellationToken.None);

        await _submissionStore.DidNotReceive().SaveAsync(Arg.Any<TypificationSubmission>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default, default!, default!, default!, default!, default!,
            default, default, default, default, default, default, default);
        await _conversationStore.DidNotReceive().SaveAsync(conv, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldCloseAndEmitSkipped_WhenPolicySchemaMutation()
    {
        // (d) breaker ON + Skip(SchemaMutation) → blank close + AutonomousSkipped(SchemaMutation), no submission.
        var conv = MakeExpiredWrapUp();
        var suggestion = MakeSuggestion(conv.ConversationId);
        SeedWrapUp(conv);

        _suggestionStore.GetLatestForConversationAsync(
                Arg.Any<EntityId>(), conv.ConversationId, Arg.Any<CancellationToken>())
            .Returns(suggestion);
        _policy.EvaluateAsync(conv, suggestion, Arg.Any<CancellationToken>())
            .Returns(AutonomousDispositionDecision.Skip(AutonomousSkipReason.SchemaMutation));

        var sut = CreateWorker(autonomousEnabled: true);

        await sut.ProcessTimeoutsAsync(CancellationToken.None);

        conv.State.Should().Be(ConversationState.Closed);
        await _conversationStore.Received(1).SaveAsync(conv, Arg.Any<CancellationToken>());
        await _submissionStore.DidNotReceive().SaveAsync(Arg.Any<TypificationSubmission>(), Arg.Any<CancellationToken>());
        await _conversationStore.DidNotReceive().TryConditionalCloseWrapUpAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        await _audit.Received(1).RecordAsync(
            Arg.Any<TenantId>(),
            "conversations",
            "typification.autonomous.skipped",
            "info",
            AutonomousActorId,
            "ai",
            conv.ConversationId.Value,
            "Conversation",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m != null && m["reason"] == "SchemaMutation"),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldEmitLicenseMissingSkip_WhenUnlicensed()
    {
        // (e) breaker ON + Skip(LicenseMissing) → blank close + AutonomousSkipped(LicenseMissing).
        var conv = MakeExpiredWrapUp();
        var suggestion = MakeSuggestion(conv.ConversationId);
        SeedWrapUp(conv);

        _suggestionStore.GetLatestForConversationAsync(
                Arg.Any<EntityId>(), conv.ConversationId, Arg.Any<CancellationToken>())
            .Returns(suggestion);
        _policy.EvaluateAsync(conv, suggestion, Arg.Any<CancellationToken>())
            .Returns(AutonomousDispositionDecision.Skip(AutonomousSkipReason.LicenseMissing));

        var sut = CreateWorker(autonomousEnabled: true);

        await sut.ProcessTimeoutsAsync(CancellationToken.None);

        conv.State.Should().Be(ConversationState.Closed);
        await _submissionStore.DidNotReceive().SaveAsync(Arg.Any<TypificationSubmission>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync(
            Arg.Any<TenantId>(),
            "conversations",
            "typification.autonomous.skipped",
            "info",
            AutonomousActorId,
            "ai",
            conv.ConversationId.Value,
            "Conversation",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m != null && m["reason"] == "LicenseMissing"),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldBeByteIdentical_WhenGateDisabledSkip()
    {
        // GateDisabled = tenant not opted in → byte-identical blank close, NO skipped audit, NO submission.
        var conv = MakeExpiredWrapUp();
        var suggestion = MakeSuggestion(conv.ConversationId);
        SeedWrapUp(conv);

        _suggestionStore.GetLatestForConversationAsync(
                Arg.Any<EntityId>(), conv.ConversationId, Arg.Any<CancellationToken>())
            .Returns(suggestion);
        _policy.EvaluateAsync(conv, suggestion, Arg.Any<CancellationToken>())
            .Returns(AutonomousDispositionDecision.Skip(AutonomousSkipReason.GateDisabled));

        var sut = CreateWorker(autonomousEnabled: true);

        await sut.ProcessTimeoutsAsync(CancellationToken.None);

        conv.State.Should().Be(ConversationState.Closed);
        await _conversationStore.Received(1).SaveAsync(conv, Arg.Any<CancellationToken>());
        await _submissionStore.DidNotReceive().SaveAsync(Arg.Any<TypificationSubmission>(), Arg.Any<CancellationToken>());
        // No audit at all for a non-opted-in tenant — byte-identical to the gate-OFF path.
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default, default!, default!, default!, default!, default!,
            default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldNotQuerySuggestionStore_WhenGateStoreReportsInactive()
    {
        // Gate-first short-circuit: when the tenant activation gate is inactive, the worker closes
        // blank WITHOUT reading the suggestion store or evaluating the policy (avoids N reads per
        // non-opted-in tenant per cycle). Behaviour is byte-identical to the GateDisabled path.
        var conv = MakeExpiredWrapUp();
        SeedWrapUp(conv);

        var gateStore = Substitute.For<ITenantAutonomousDispositionStore>();
        gateStore.GetAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns((TenantAutonomousDisposition?)null); // never opted in → inactive

        var sut = CreateWorker(autonomousEnabled: true, gateStore: gateStore);

        await sut.ProcessTimeoutsAsync(CancellationToken.None);

        // Closed blank, and the expensive reads never happened.
        conv.State.Should().Be(ConversationState.Closed);
        await _conversationStore.Received(1).SaveAsync(conv, Arg.Any<CancellationToken>());
        await _suggestionStore.DidNotReceiveWithAnyArgs().GetLatestForConversationAsync(
            default!, default!, default);
        await _policy.DidNotReceiveWithAnyArgs().EvaluateAsync(default!, default, default);
        await _submissionStore.DidNotReceive().SaveAsync(Arg.Any<TypificationSubmission>(), Arg.Any<CancellationToken>());
        // No audit at all for a non-opted-in tenant — byte-identical to the gate-OFF path.
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default, default!, default!, default!, default!, default!,
            default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldQuerySuggestionStore_WhenGateStoreReportsActive()
    {
        // When the gate IS active the short-circuit does not fire — the suggestion read + policy
        // evaluation proceed exactly as before.
        var conv = MakeExpiredWrapUp();
        var suggestion = MakeSuggestion(conv.ConversationId);
        SeedWrapUp(conv);

        var gateStore = Substitute.For<ITenantAutonomousDispositionStore>();
        gateStore.GetAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new TenantAutonomousDisposition
            {
                TenantId = new TenantId(TestTenantId),
                AttestedByUserId = "admin-1",
                AttestedAt = _now.AddDays(-1),
            }); // active (not revoked)

        _suggestionStore.GetLatestForConversationAsync(
                Arg.Any<EntityId>(), conv.ConversationId, Arg.Any<CancellationToken>())
            .Returns(suggestion);
        _policy.EvaluateAsync(conv, suggestion, Arg.Any<CancellationToken>())
            .Returns(AutonomousDispositionDecision.Commit(
                EntityId.From("leaf-9"), ["root", "sales", "completed"], 0.97));
        _conversationStore.TryConditionalCloseWrapUpAsync(
                Arg.Any<TenantId>(), conv.ConversationId, _now, Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = CreateWorker(autonomousEnabled: true, gateStore: gateStore);

        await sut.ProcessTimeoutsAsync(CancellationToken.None);

        await _suggestionStore.Received(1).GetLatestForConversationAsync(
            Arg.Any<EntityId>(), conv.ConversationId, Arg.Any<CancellationToken>());
        await _submissionStore.Received(1).SaveAsync(Arg.Any<TypificationSubmission>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldHonourPerCycleCap_WhenCommitsExceedCap()
    {
        // (f) cap = 2, three committable candidates → exactly 2 stamped, the 3rd left in WrapUp (not closed).
        var convs = new[] { MakeExpiredWrapUp(), MakeExpiredWrapUp(), MakeExpiredWrapUp() };
        SeedWrapUp(convs);

        foreach (var c in convs)
        {
            var s = MakeSuggestion(c.ConversationId);
            _suggestionStore.GetLatestForConversationAsync(
                    Arg.Any<EntityId>(), c.ConversationId, Arg.Any<CancellationToken>())
                .Returns(s);
            _policy.EvaluateAsync(c, s, Arg.Any<CancellationToken>())
                .Returns(AutonomousDispositionDecision.Commit(
                    EntityId.From("leaf-9"), ["root", "sales", "completed"], 0.97));
            _conversationStore.TryConditionalCloseWrapUpAsync(
                    Arg.Any<TenantId>(), c.ConversationId, _now, Arg.Any<CancellationToken>())
                .Returns(true);
        }

        var sut = CreateWorker(autonomousEnabled: true, perCycleCap: 2);

        await sut.ProcessTimeoutsAsync(CancellationToken.None);

        // At most 2 stamps + 2 conditional closes; the 3rd candidate is deferred (no CAS, no stamp).
        await _submissionStore.Received(2).SaveAsync(Arg.Any<TypificationSubmission>(), Arg.Any<CancellationToken>());
        await _conversationStore.Received(2).TryConditionalCloseWrapUpAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), _now, Arg.Any<CancellationToken>());
    }
}
