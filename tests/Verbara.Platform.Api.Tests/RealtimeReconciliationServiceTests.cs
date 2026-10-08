using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class RealtimeReconciliationServiceTests
{
    private const string TestTenantId = "t-recon";
    private const string TestQueueName = "q-support";

    [Fact]
    public async Task ReconcileAsync_ShouldReSyncMembership_WhenRowExistsInVerbara()
    {
        // Forward-only convergent reconciler: every non-excluded membership in
        // Verbara is re-issued to IRealtimeSyncService.AddQueueMemberAsync.
        // The SDK Pro upsert is idempotent so this catches up any silently
        // swallowed writes from the foreground call sites.
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-recon");
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId, penalty: 0));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        await sync.Received(1).AddQueueMemberAsync(
            TestTenantId, TestQueueName, "agent-recon", agent.DisplayName,
            0, Arg.Is<IReadOnlyList<string>?>(x => x == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_ShouldSkipMembership_WhenIsExcludedTrue()
    {
        // IsExcluded=true → membership row is decorative (audit / future
        // re-include). It MUST NOT be re-issued to Asterisk; otherwise the
        // gate semantics from Phase B leak into the sync path.
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-excluded");
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId, isExcluded: true));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        await sync.DidNotReceiveWithAnyArgs().AddQueueMemberAsync(
            default!, default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task ReconcileAsync_ShouldForwardAllowedChannels_SoSdkVoiceGateApplies()
    {
        // Phase B v2.6.0-pro pushed the voice-gate INTO the SDK; the reconciler
        // just passes AllowedChannels through. A membership with
        // AllowedChannels=["WebChat"] surfaces the list verbatim — the SDK
        // short-circuits to RemoveQueueMemberAsync because "voice" is absent.
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-webchat");
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(
            agent.AgentId, queue.QueueId,
            allowedChannels: ["WebChat"]));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        await sync.Received(1).AddQueueMemberAsync(
            TestTenantId, TestQueueName, "agent-webchat", agent.DisplayName,
            0,
            Arg.Is<IReadOnlyList<string>?>(x => x != null && x.Count == 1 && x[0] == "WebChat"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_ShouldNotThrow_WhenSyncServiceUnavailable()
    {
        // When Pro.Realtime is not wired (no connection string) the
        // IRealtimeSyncService is not registered. The reconciler must skip
        // the tick cleanly rather than crashing the worker.
        var heartbeat = new ServiceHeartbeat();
        var options = Options.Create(new RealtimeOptions { ReconcilerIntervalSeconds = 60 });

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ITenantStore>());
        services.AddSingleton(Substitute.For<IQueueMembershipStore>());
        services.AddSingleton(Substitute.For<IQueueStore>());
        services.AddSingleton(Substitute.For<IAgentStore>());
        // IRealtimeSyncService deliberately NOT registered.

        var sp = services.BuildServiceProvider();
        var sut = new RealtimeReconciliationService(
            sp, heartbeat, options, new AgentPauseCoordinator(),
            NullLogger<RealtimeReconciliationService>.Instance);

        var act = async () => await sut.ReconcileAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReconcileTenantAsync_ShouldSyncAgentUnpaused_WhenAgentIsAvailable()
    {
        // Regression (queue-members-stay-unpaused-across-reconcile): AddQueueMemberAsync
        // writes paused=1 on insert, so after an agent's upserts the tick must re-assert
        // the agent's real pause state. An Available agent with no pending pause ends unpaused.
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-available");
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        // The Add (paused=1 on insert) must be followed by the convergence write.
        RealtimeSyncCallLog.Of(sync).Should().Equal("AddQueueMemberAsync", "SyncAgentPausedAsync:False");
        await sync.Received(1).SyncAgentPausedAsync(TestTenantId, "agent-available", false, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(AgentState.Break, true)]
    [InlineData(AgentState.ACW, true)]
    [InlineData(AgentState.Offline, true)]
    [InlineData(AgentState.Busy, false)]
    public async Task ReconcileTenantAsync_ShouldSyncRulePausedValue_ForAgentState(AgentState state, bool expectedPaused)
    {
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-state");
        agent.State = state;
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        RealtimeSyncCallLog.Of(sync).Should().Equal("AddQueueMemberAsync", $"SyncAgentPausedAsync:{expectedPaused}");
    }

    [Fact]
    public async Task ReconcileTenantAsync_ShouldSyncAgentPaused_WhenAvailableAgentHasPendingPause()
    {
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-pending");
        agent.PendingState = AgentState.Break;
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        RealtimeSyncCallLog.Of(sync).Should().Equal("AddQueueMemberAsync", "SyncAgentPausedAsync:True");
    }

    [Fact]
    public async Task ReconcileTenantAsync_ShouldConvergeOncePerAgent_AfterItsLastUpsert()
    {
        // SyncAgentPausedAsync sets every row of the agent's interface, so one write per agent per
        // pass, after the agent's last AddQueueMemberAsync, covers all its queues.
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-two-queues");
        var q1 = MakeQueue(EntityId.From("q-1"));
        var q2 = MakeQueue(EntityId.From("q-2"));
        harness.SeedAgent(agent);
        harness.SeedQueue(q1);
        harness.SeedQueue(q2);
        harness.SeedMembership(MakeMembership(agent.AgentId, q1.QueueId));
        harness.SeedMembership(MakeMembership(agent.AgentId, q2.QueueId));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        RealtimeSyncCallLog.Of(sync).Should().Equal(
            "AddQueueMemberAsync", "AddQueueMemberAsync", "SyncAgentPausedAsync:False");
    }

    [Fact]
    public async Task ReconcileTenantAsync_ShouldNotConverge_WhenAgentOnlyHasExcludedMemberships()
    {
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-excluded-only");
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId, isExcluded: true));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        await sync.DidNotReceiveWithAnyArgs().SyncAgentPausedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ReconcileTenantAsync_ShouldNotConverge_WhenMembershipIsDigitalOnly()
    {
        // Voice gate: a digital-only membership has no queue_members row (the SDK removes it).
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-digital");
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId, allowedChannels: ["WebChat"]));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        await sync.DidNotReceiveWithAnyArgs().SyncAgentPausedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ReconcileTenantAsync_ShouldNotConverge_WhenUpsertThrows()
    {
        var harness = BuildHarness(out var sync);
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        sync.AddQueueMemberAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(_ => new ValueTask(Task.FromException(new InvalidOperationException("boom"))));
#pragma warning restore CA2012
        var agent = MakeAgent("agent-add-fails");
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        await sync.DidNotReceiveWithAnyArgs().SyncAgentPausedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ReconcileTenantAsync_ShouldWriteFreshState_WhenAgentChangesStateDuringThePass()
    {
        // A pass that started while the agent was Break; the agent becomes Available before the
        // convergence. The convergence re-reads the agent, so the newer state wins over the tick cache.
        var harness = BuildHarness(out var sync);
        var agent = MakeAgent("agent-racing");
        agent.State = AgentState.Break;
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        sync.AddQueueMemberAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(_ =>
            {
                harness.ReplaceAgent(MakeAgent("agent-racing")); // fresh row, State = Available
                return ValueTask.CompletedTask;
            });
#pragma warning restore CA2012
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        RealtimeSyncCallLog.Of(sync).Should().Equal("AddQueueMemberAsync", "SyncAgentPausedAsync:False");
    }

    [Fact]
    public async Task ReconcileTenantAsync_ShouldReadTheUndecoratedAgentStore_WhenItIsRegistered()
    {
        var undecorated = Substitute.For<IAgentStore>();
        var harness = BuildHarness(out var sync, undecorated);
        var agent = MakeAgent("agent-inner");
        var innerView = MakeAgent("agent-inner");
        innerView.State = AgentState.DND;
        undecorated.GetByIdAsync(Arg.Any<TenantId>(), agent.AgentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Agent?>(innerView));
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(agent);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(agent.AgentId, queue.QueueId));

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        RealtimeSyncCallLog.Of(sync).Should().Equal("AddQueueMemberAsync", "SyncAgentPausedAsync:True");
    }

    [Fact]
    public async Task ReconcileTenantAsync_ShouldKeepConvergingOtherAgents_WhenOneConvergenceThrows()
    {
        var harness = BuildHarness(out var sync);
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        sync.SyncAgentPausedAsync(TestTenantId, "agent-a", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("db down"))));
#pragma warning restore CA2012
        var a = MakeAgent("agent-a");
        var b = MakeAgent("agent-b");
        var queue = MakeQueue(EntityId.From("q-1"));
        harness.SeedAgent(a);
        harness.SeedAgent(b);
        harness.SeedQueue(queue);
        harness.SeedMembership(MakeMembership(a.AgentId, queue.QueueId));
        harness.SeedMembership(MakeMembership(b.AgentId, queue.QueueId));

        var act = async () => await harness.Sut.ReconcileAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await sync.Received(1).SyncAgentPausedAsync(TestTenantId, "agent-b", false, Arg.Any<CancellationToken>());
    }

    // ── licensed-agent-metering: members of an agent whose user is not Active ─

    [Fact]
    public async Task ReconcileTenantAsync_ShouldNotReissueMembers_WhenAgentUserIsNotActive()
    {
        var harness = BuildHarness(out var sync);
        var queueId = EntityId.New();
        harness.SeedQueue(MakeQueue(queueId));
        var suspended = MakeAgent("agent-suspended");
        var active = MakeAgent("agent-active");
        harness.SeedAgent(suspended);
        harness.SeedAgent(active);
        harness.SeedMembership(MakeMembership(suspended.AgentId, queueId));
        harness.SeedMembership(MakeMembership(active.AgentId, queueId));
        harness.AccountStatus.Inactive.Add(suspended.UserId);

        await harness.Sut.ReconcileAsync(CancellationToken.None);

        // Re-adding the row would undo the desired-state removal (realtime-queue-member-sync).
        await sync.DidNotReceive().AddQueueMemberAsync(
            TestTenantId, Arg.Any<string>(), "agent-suspended", Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>());
        await sync.DidNotReceive().SyncAgentPausedAsync(TestTenantId, "agent-suspended", Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await sync.Received(1).AddQueueMemberAsync(
            TestTenantId, TestQueueName, "agent-active", Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>());
    }

    // ── Test harness ──────────────────────────────────────────────────────

    private static Harness BuildHarness(out IRealtimeSyncService sync, IAgentStore? undecoratedAgentStore = null)
    {
        sync = Substitute.For<IRealtimeSyncService>();
        sync.Events.Returns(System.Reactive.Linq.Observable.Empty<RealtimeSyncEvent>());
        return new Harness(sync, undecoratedAgentStore);
    }

    private sealed class Harness
    {
        private readonly List<Agent> _agents = [];
        private readonly List<Queue> _queues = [];
        private readonly List<QueueMembership> _memberships = [];
        private readonly IAgentStore _agentStore = Substitute.For<IAgentStore>();
        private readonly IQueueStore _queueStore = Substitute.For<IQueueStore>();
        private readonly IQueueMembershipStore _membershipStore = Substitute.For<IQueueMembershipStore>();
        private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();

        public RealtimeReconciliationService Sut { get; }

        public AgentPauseCoordinator Coordinator { get; } = new();

        public FakeAgentAccountStatusLookup AccountStatus { get; } = new();

        public Harness(IRealtimeSyncService sync, IAgentStore? undecoratedAgentStore = null)
        {
            _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
                .Returns(new[] { new Tenant { TenantId = TestTenantId, Name = "Recon", Status = TenantStatus.Active } });

            _agentStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = call.ArgAt<EntityId>(1);
                    return Task.FromResult<Agent?>(_agents.FirstOrDefault(a => a.AgentId == id));
                });
            _agentStore.GetByIdsAsync(Arg.Any<TenantId>(), Arg.Any<IReadOnlyCollection<EntityId>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var ids = call.ArgAt<IReadOnlyCollection<EntityId>>(1);
                    return Task.FromResult<IReadOnlyList<Agent>>(_agents.Where(a => ids.Contains(a.AgentId)).ToList());
                });
            _queueStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = call.ArgAt<EntityId>(1);
                    return Task.FromResult<Queue?>(_queues.FirstOrDefault(q => q.QueueId == id));
                });
            _membershipStore.ListByTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IReadOnlyList<QueueMembership>>(_memberships));

            var services = new ServiceCollection();
            services.AddSingleton(_tenantStore);
            services.AddSingleton(_membershipStore);
            services.AddSingleton(_queueStore);
            services.AddSingleton(_agentStore);
            services.AddSingleton(sync);
            services.AddSingleton<Verbara.Platform.Queues.Services.IAgentAccountStatusLookup>(AccountStatus);
            if (undecoratedAgentStore is not null)
            {
                services.AddKeyedSingleton(
                    Verbara.Platform.Api.DependencyInjection.RealtimeSyncingStoresExtensions.AgentStoreInner,
                    undecoratedAgentStore);
            }

            Sut = new RealtimeReconciliationService(
                services.BuildServiceProvider(),
                new ServiceHeartbeat(),
                Options.Create(new RealtimeOptions { ReconcilerIntervalSeconds = 60 }),
                Coordinator,
                NullLogger<RealtimeReconciliationService>.Instance);
        }

        public void SeedAgent(Agent a) => _agents.Add(a);
        public void ReplaceAgent(Agent a)
        {
            _agents.RemoveAll(x => x.AgentId == a.AgentId);
            _agents.Add(a);
        }
        public void SeedQueue(Queue q) => _queues.Add(q);
        public void SeedMembership(QueueMembership m) => _memberships.Add(m);
    }

    private static Agent MakeAgent(string id) => new()
    {
        AgentId = EntityId.From(id),
        TenantId = new TenantId(TestTenantId),
        UserId = EntityId.New(),
        DisplayName = $"Agent {id}",
        State = AgentState.Available,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Queue MakeQueue(EntityId queueId) => new()
    {
        QueueId = queueId,
        TenantId = new TenantId(TestTenantId),
        Name = TestQueueName,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static QueueMembership MakeMembership(
        EntityId agentId,
        EntityId queueId,
        int penalty = 0,
        bool isExcluded = false,
        IReadOnlyList<string>? allowedChannels = null) => new()
    {
        TenantId = new TenantId(TestTenantId),
        QueueId = queueId,
        AgentId = agentId,
        Penalty = penalty,
        Source = MembershipSource.Manual,
        IsExcluded = isExcluded,
        CreatedAt = DateTimeOffset.UtcNow,
        AllowedChannels = allowedChannels,
    };
}
