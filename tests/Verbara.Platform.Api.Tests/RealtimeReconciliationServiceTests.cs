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
            sp, heartbeat, options,
            NullLogger<RealtimeReconciliationService>.Instance);

        var act = async () => await sut.ReconcileAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── Test harness ──────────────────────────────────────────────────────

    private static Harness BuildHarness(out IRealtimeSyncService sync)
    {
        sync = Substitute.For<IRealtimeSyncService>();
        sync.Events.Returns(System.Reactive.Linq.Observable.Empty<RealtimeSyncEvent>());
        return new Harness(sync);
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

        public Harness(IRealtimeSyncService sync)
        {
            _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
                .Returns(new[] { new Tenant { TenantId = TestTenantId, Name = "Recon", Status = TenantStatus.Active } });

            _agentStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = call.ArgAt<EntityId>(1);
                    return Task.FromResult<Agent?>(_agents.FirstOrDefault(a => a.AgentId == id));
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

            Sut = new RealtimeReconciliationService(
                services.BuildServiceProvider(),
                new ServiceHeartbeat(),
                Options.Create(new RealtimeOptions { ReconcilerIntervalSeconds = 60 }),
                NullLogger<RealtimeReconciliationService>.Instance);
        }

        public void SeedAgent(Agent a) => _agents.Add(a);
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
