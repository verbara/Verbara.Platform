using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions, against real Postgres stores
/// (<see cref="DispositionDispatchPostgresFixture"/>): the durable hand-off of an agent's disposition (design D16) — a
/// failed or interrupted hand-off is completed by a dispatcher, once, and announced once — and the wrap-up timeout
/// that keeps a stored typification (design D17, Platform #476).
/// </summary>
[Collection(DispositionDispatchPostgresSuite.Name)]
public sealed class DispositionDispatchLiveDbTests : IAsyncLifetime
{
    private readonly DispositionDispatchPostgresFixture _fixture;
    private readonly ConcurrentQueue<PlatformEvent> _events = new();
    private IDisposable? _subscription;

    public DispositionDispatchLiveDbTests(DispositionDispatchPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        _subscription = _fixture.Services.GetRequiredService<PlatformEventBus>().Events.Subscribe(new Observer(_events));
    }

    public Task DisposeAsync()
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private int DispositionEvents => _events.OfType<CampaignDispositionSubmittedEvent>().Count();

    private static string NewConversation() => $"conv-{Guid.NewGuid():N}";

    /// <summary>The endpoint's write: the submission and its pre-claimed dispatch, in one transaction.</summary>
    private async Task<Typification.DialerDispositionDispatch> CommitTypificationAsync(string conversation)
    {
        var dispatch = _fixture.PreClaimed(conversation);
        (await _fixture.Submissions.TryInsertAsync(
            DispositionDispatchPostgresFixture.Submission(conversation, "leaf-agent"), dispatch, CancellationToken.None))
            .Should().BeTrue();
        return dispatch;
    }

    // ─── B.10: the hand-off ─────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_ShouldLeaveThePendingRowForTheWorker_WhenTheSeamThrowsAfterTheCommit()
    {
        var conversation = NewConversation();
        var dispatch = await CommitTypificationAsync(conversation);
        _fixture.Campaigns.ScriptAgentDisposition(1, AgentDispositionResult.Applied);

        var outcome = await _fixture.Dispatcher.DispatchAsync(dispatch, dispatch.ClaimId!, CancellationToken.None);

        outcome.Should().Be(DispatchOutcome.Deferred);
        var released = (await _fixture.RowAsync(conversation))!.Value;
        released.Attempts.Should().Be(1);
        released.Result.Should().BeNull();
        released.ClaimId.Should().BeNull();
        released.NextAttemptAt.Should().Be(DispositionDispatchPostgresFixture.Epoch.AddSeconds(5));
        DispositionEvents.Should().Be(0);

        _fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await _fixture.NewWorker().ProcessOnceAsync(CancellationToken.None);

        _fixture.Campaigns.AgentDispositionCalls.Should().HaveCount(2, "the request's call and the worker's, no more");
        _fixture.Campaigns.AgentDispositionEffects.Should().Be(1);
        var completed = (await _fixture.RowAsync(conversation))!.Value;
        completed.Result.Should().Be("Applied");
        completed.Attempts.Should().Be(2);
        DispositionEvents.Should().Be(1);
    }

    [Fact]
    public async Task ProcessOnceAsync_ShouldHandOverACommittedRowNoRequestEverDispatched_WhenTheInstanceStoppedAfterTheCommit()
    {
        var conversation = NewConversation();
        await CommitTypificationAsync(conversation);
        _fixture.Clock.Advance(DialerDispositionDispatcher.Lease + TimeSpan.FromSeconds(1));

        await _fixture.NewWorker().ProcessOnceAsync(CancellationToken.None);

        _fixture.Campaigns.AgentDispositionCalls.Should().ContainSingle();
        _fixture.Campaigns.AgentDispositionEffects.Should().Be(1);
        (await _fixture.RowAsync(conversation))!.Value.Result.Should().Be("Applied");
        DispositionEvents.Should().Be(1);
    }

    [Fact]
    public async Task ProcessOnceAsync_ShouldCompleteWithoutASecondEffect_WhenTheSeamAnswersAlreadyApplied()
    {
        var conversation = NewConversation();
        var dispatch = await CommitTypificationAsync(conversation);
        (await _fixture.Dispatches.ReleaseAsync(
            dispatch, "lost answer", DispositionDispatchPostgresFixture.Epoch, dispatch.ClaimId!, CancellationToken.None))
            .Should().BeTrue();
        _fixture.Campaigns.ScriptAgentDisposition(0, AgentDispositionResult.AlreadyApplied);

        await _fixture.NewWorker().ProcessOnceAsync(CancellationToken.None);

        _fixture.Campaigns.AgentDispositionEffects.Should().Be(0);
        var row = (await _fixture.RowAsync(conversation))!.Value;
        row.Attempts.Should().Be(2);
        row.Result.Should().Be("AlreadyApplied");
        row.PublishedAt.Should().NotBeNull("an earlier claim did not complete it, so it was never announced");
        DispositionEvents.Should().Be(1);
    }

    [Fact]
    public async Task ProcessOnceAsync_ShouldLeaveTheRow_WhenAnotherClaimIsLive()
    {
        var conversation = NewConversation();
        await CommitTypificationAsync(conversation);
        _fixture.Clock.Advance(TimeSpan.FromSeconds(30));

        await _fixture.NewWorker().ProcessOnceAsync(CancellationToken.None);

        _fixture.Campaigns.AgentDispositionCalls.Should().BeEmpty();
        (await _fixture.RowAsync(conversation))!.Value.Result.Should().BeNull();
    }

    [Fact]
    public async Task DispatchAsync_ShouldApplyAndAnnounceOnce_WhenTheRequestOutlivesItsLeaseAndAWorkerTakesOver()
    {
        var conversation = NewConversation();
        var dispatch = await CommitTypificationAsync(conversation);
        var requestHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Campaigns.ScriptAgentDisposition(async (_, _) =>
        {
            requestHeld.SetResult();
            await releaseRequest.Task;
            return AgentDispositionResult.Applied;
        });
        _fixture.Campaigns.ScriptAgentDisposition(0, AgentDispositionResult.AlreadyApplied);

        var request = _fixture.Dispatcher.DispatchAsync(dispatch, dispatch.ClaimId!, CancellationToken.None);
        await requestHeld.Task;
        _fixture.Clock.Advance(DialerDispositionDispatcher.Lease + TimeSpan.FromSeconds(1));
        await _fixture.NewWorker().ProcessOnceAsync(CancellationToken.None);
        releaseRequest.SetResult();
        var requestOutcome = await request;

        requestOutcome.Should().Be(DispatchOutcome.Applied, "the request reports the dialer's answer to it");
        _fixture.Campaigns.AgentDispositionEffects.Should().Be(1);
        var row = (await _fixture.RowAsync(conversation))!.Value;
        row.Result.Should().Be("AlreadyApplied", "the worker's completion stands; the request's was fenced off");
        DispositionEvents.Should().Be(1);
    }

    [Fact]
    public async Task ProcessOnceAsync_ShouldClaimEachRowOnce_WhenTwoWorkersPollTogether()
    {
        var conversations = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var conversation = NewConversation();
            await CommitTypificationAsync(conversation);
            conversations.Add(conversation);
        }

        _fixture.Clock.Advance(DialerDispositionDispatcher.Lease + TimeSpan.FromSeconds(1));
        await Task.WhenAll(
            Task.Run(() => _fixture.NewWorker().ProcessOnceAsync(CancellationToken.None)),
            Task.Run(() => _fixture.NewWorker().ProcessOnceAsync(CancellationToken.None)));

        _fixture.Campaigns.AgentDispositionCalls.Select(c => c.ActionId).Should().OnlyHaveUniqueItems()
            .And.HaveCount(conversations.Count);
        DispositionEvents.Should().Be(conversations.Count);
    }

    // ─── B.12 (#476, design D17): the wrap-up timeout against the typification's write ─────────

    private ConversationTimeoutWorker NewTimeoutWorker(IAuditService audit, AutonomousDispositionMetrics metrics)
    {
        var tenants = Substitute.For<ITenantStore>();
        tenants.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Tenant { TenantId = DispositionDispatchPostgresFixture.TenantId, Name = "Dispatch", Status = TenantStatus.Active },
        });
        // The policy is not under test: every expired WrapUp with a suggestion qualifies (an active gate and a licence).
        var policy = Substitute.For<IAutonomousDispositionPolicy>();
        policy.EvaluateAsync(Arg.Any<Conversation>(), Arg.Any<AiSuggestionRecord?>(), Arg.Any<CancellationToken>())
            .Returns(AutonomousDispositionDecision.Commit(EntityId.From("leaf-ai"), ["root", "leaf-ai"], 0.97));
        var services = _fixture.Services;
        return new ConversationTimeoutWorker(
            services.GetRequiredService<IConversationStore>(),
            tenants,
            Substitute.For<IConversationSwitchboard>(),
            services.GetRequiredService<PlatformEventBus>(),
            _fixture.Clock,
            new Verbara.Platform.Api.Health.ServiceHeartbeat(),
            Options.Create(new DistributionOptions { AutonomousDispositionEnabled = true }),
            NullLogger<ConversationTimeoutWorker>.Instance,
            policy: null,
            autonomousPolicy: policy,
            suggestionStore: services.GetRequiredService<IAiSuggestionStore>(),
            submissionStore: _fixture.Submissions,
            auditService: audit,
            autonomousMetrics: metrics,
            gateStore: null);
    }

    /// <summary>An expired voice campaign Conversation in WrapUp with a qualifying suggestion.</summary>
    private async Task<string> SeedExpiredWrapUpAsync()
    {
        var id = NewConversation();
        var tenant = new TenantId(DispositionDispatchPostgresFixture.TenantId);
        var conversation = new Conversation
        {
            ConversationId = EntityId.From(id),
            TenantId = tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = ConversationState.WrapUp,
            CreatedAt = _fixture.Clock.UtcNow.AddMinutes(-10),
            UpdatedAt = _fixture.Clock.UtcNow.AddSeconds(-200),
        };
        conversation.SetMetadata("campaignId", "11");
        conversation.SetMetadata("dialerActionId", $"action-{id}");
        await _fixture.Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        await _fixture.Services.GetRequiredService<IAiSuggestionStore>().SaveAsync(new AiSuggestionRecord
        {
            Id = EntityId.New(),
            TenantId = EntityId.From(tenant.Value),
            ConversationId = conversation.ConversationId,
            SchemaId = EntityId.From("schema-1"),
            SchemaVersion = 1,
            SuggestedLeafNodeId = EntityId.From("leaf-ai"),
            SuggestedNodePath = ["root", "leaf-ai"],
            SuggestedFieldValues = new Dictionary<string, string>(),
            Confidence = 0.97,
            ModelId = "gpt-test",
            PromptVersion = "v1",
            SurfacedBand = TypificationBand.None,
            CreatedAt = _fixture.Clock.UtcNow.AddMinutes(-5),
        }, CancellationToken.None);
        return id;
    }

    private async Task<(ConversationState State, TypificationSubmission? Submission)> StoredAsync(string id)
    {
        var tenant = new TenantId(DispositionDispatchPostgresFixture.TenantId);
        var conversation = await _fixture.Services.GetRequiredService<IConversationStore>()
            .GetByIdAsync(tenant, EntityId.From(id), CancellationToken.None);
        var submission = await _fixture.Submissions.GetByConversationIdAsync(tenant, EntityId.From(id), CancellationToken.None);
        return (conversation!.State, submission);
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldKeepTheAgentsSubmissionAndDispatch_WhenTheTypificationCommittedFirst()
    {
        var id = await SeedExpiredWrapUpAsync();
        await CommitTypificationAsync(id);
        using var metrics = new AutonomousDispositionMetrics();

        await NewTimeoutWorker(Substitute.For<IAuditService>(), metrics).ProcessTimeoutsAsync(CancellationToken.None);

        var (state, submission) = await StoredAsync(id);
        state.Should().Be(ConversationState.Closed);
        submission!.LeafNodeId.Value.Should().Be("leaf-agent");
        submission.Source.Should().Be(SubmissionSource.Manual);
        (await _fixture.CountAsync("dialer_disposition_dispatches", id)).Should().Be(1);
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldStoreOneConsistentDisposition_WhenATypificationRacesTheTimeout()
    {
        using var metrics = new AutonomousDispositionMetrics();
        var worker = NewTimeoutWorker(Substitute.For<IAuditService>(), metrics);
        var agentWon = 0;
        var timeoutWon = 0;

        for (var iteration = 0; iteration < 50; iteration++)
        {
            var id = await SeedExpiredWrapUpAsync();
            using var barrier = new Barrier(2);
            // Every other iteration the typification starts a little later, so both orders of the two writes occur.
            var typifyDelay = TimeSpan.FromMilliseconds(iteration % 2 == 0 ? 0 : iteration % 7 * 4);
            var typify = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await Task.Delay(typifyDelay); // fence-allow: SIMULATED-WORK — staggers the typification so both orders of the two writes occur
                return await _fixture.Submissions.TryInsertAsync(
                    DispositionDispatchPostgresFixture.Submission(id, "leaf-agent"), _fixture.PreClaimed(id), CancellationToken.None);
            });
            var timeout = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await worker.ProcessTimeoutsAsync(CancellationToken.None);
            });
            await Task.WhenAll(typify, timeout);

            var (state, submission) = await StoredAsync(id);
            state.Should().Be(ConversationState.Closed, "iteration {0}", iteration);
            (await _fixture.CountAsync("typification_submissions", id)).Should().Be(1, "iteration {0}", iteration);
            var agents = submission!.LeafNodeId.Value == "leaf-agent";
            (await typify).Should().Be(agents, "the agent's write answers 200 exactly when its submission is the stored one");
            (await _fixture.CountAsync("dialer_disposition_dispatches", id)).Should().Be(agents ? 1 : 0,
                "a dispatch row exists exactly when the stored submission is the agent's (iteration {0})", iteration);
            if (agents) agentWon++;
            else timeoutWon++;
        }

        // Recorded in tasks.md (B.12): how many iterations each side won.
        Console.WriteLine($"[B.12 race] agent won {agentWon}, timeout won {timeoutWon} of 50");
        (agentWon + timeoutWon).Should().Be(50);
    }

    // round-6 audit F2: the AlreadyTypified shortcut closes with the conditional close, so two replicas sweeping the same
    // typified Conversation announce, audit and count it once.
    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldAuditOneSkip_WhenTwoWorkersCloseTheSameTypifiedConversation()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var id = await SeedExpiredWrapUpAsync();
            await CommitTypificationAsync(id);
            var audit = Substitute.For<IAuditService>();
            using var metrics = new AutonomousDispositionMetrics();
            using var skips = new TypifyEndpointTests.InstrumentCounter(metrics.Skipped);
            var a = NewTimeoutWorker(audit, metrics);
            var b = NewTimeoutWorker(audit, metrics);
            var closedBefore = ClosedEventsFor(id);

            using var barrier = new Barrier(2);
            await Task.WhenAll(
                Task.Run(async () => { barrier.SignalAndWait(); await a.ProcessTimeoutsAsync(CancellationToken.None); }),
                Task.Run(async () => { barrier.SignalAndWait(); await b.ProcessTimeoutsAsync(CancellationToken.None); }));

            (await StoredAsync(id)).State.Should().Be(ConversationState.Closed);
            (ClosedEventsFor(id) - closedBefore).Should().Be(1, "one close announcement (iteration {0})", iteration);
            await audit.Received(1).RecordAsync(
                Arg.Any<TenantId>(), "conversations", "typification.autonomous.skipped", "info",
                Arg.Any<string>(), "ai", id, "Conversation", Arg.Any<Guid?>(), Arg.Any<AuditChanges?>(),
                Arg.Is<IReadOnlyDictionary<string, string>>(m => m != null && m["reason"] == "AlreadyTypified"),
                Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
            skips.Total.Should().Be(1);
        }
    }

    private int ClosedEventsFor(string id) =>
        _events.OfType<ConversationStateChangedEvent>().Count(e => e.ConversationId == id && e.NewState == "Closed");

    private sealed class Observer(ConcurrentQueue<PlatformEvent> sink) : IObserver<PlatformEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(PlatformEvent value) => sink.Enqueue(value);
    }
}
