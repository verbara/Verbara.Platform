using System.Reactive.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// W4 — unit tests for the leader-gated deferred-pause drain worker. The service is
/// exercised through its public <c>SweepOnceAsync</c> hook so the loop's
/// <see cref="PeriodicTimer"/> never has to fire (deterministic + fast).
/// </summary>
public sealed class PendingPauseDrainWorkerTests
{
    private const string Tenant = "tenant-1";
    private const string TenantB = "tenant-2";

    [Fact]
    public async Task SweepOnceAsync_ShouldNoOp_WhenNotLeader()
    {
        var ctx = new TestContext(isLeader: false);
        var agent = await ctx.SeedPendingAgentAsync(Tenant, AgentState.Busy, AgentState.Break);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Busy);
        refreshed.HasPendingPause.Should().BeTrue();
        ctx.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldApplyPending_WhenNoActiveWork()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedPendingAgentAsync(Tenant, AgentState.Busy, AgentState.Break);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Break);
        refreshed.HasPendingPause.Should().BeFalse();

        ctx.Events.Should().ContainSingle();
        var evt = ctx.Events[0];
        evt.NewState.Should().Be("Break");
        evt.OldState.Should().Be("Busy");
        evt.AgentId.Should().Be(agent.AgentId.Value);
        evt.TenantId.Should().Be(Tenant);
        // No-flicker contract: apply must NOT publish a pending-state-changed event.
        ctx.PendingEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldNotApply_WhenActiveWorkRemainsUnderTimeout()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedPendingAgentAsync(
            Tenant, AgentState.Busy, AgentState.Break, pendingSince: ctx.Now);
        await ctx.SeedActiveWorkAsync(Tenant, agent.AgentId);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Busy);   // still working
        refreshed.HasPendingPause.Should().BeTrue();     // still pending
        ctx.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldForceApplyAndAudit_WhenTimeoutExceededWithActiveWork()
    {
        var ctx = new TestContext();
        // PendingSince far in the past (1 hour ago) → beyond the 30-minute tenant default.
        var stale = ctx.Now - TimeSpan.FromHours(1);
        var agent = await ctx.SeedPendingAgentAsync(
            Tenant, AgentState.Busy, AgentState.Break, pendingSince: stale);
        await ctx.SeedActiveWorkAsync(Tenant, agent.AgentId);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Break);     // force-applied despite stuck work
        refreshed.HasPendingPause.Should().BeFalse();
        ctx.Events.Should().ContainSingle();

        await ctx.Audit.Received(1).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == Tenant),
            "queues",
            "agent.pending_pause.forced_timeout",
            "warning",
            "system",
            "system",
            agent.AgentId.Value,
            "Agent",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m != null &&
                m["applied_state"] == "Break"
                && int.Parse(m["stuck_active_work_count"], System.Globalization.CultureInfo.InvariantCulture) > 0
                && m["pending_since"].Length > 0),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldSkipTimeout_WhenTenantTimeoutZero()
    {
        var ctx = new TestContext();
        await ctx.SetTenantTimeoutAsync(Tenant, timeoutMinutes: 0);
        var stale = ctx.Now - TimeSpan.FromHours(1);
        var agent = await ctx.SeedPendingAgentAsync(
            Tenant, AgentState.Busy, AgentState.Break, pendingSince: stale);
        await ctx.SeedActiveWorkAsync(Tenant, agent.AgentId);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Busy);   // timeout disabled → never force-applied
        refreshed.HasPendingPause.Should().BeTrue();
        ctx.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldBeIdempotent_WhenRunTwice()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedPendingAgentAsync(Tenant, AgentState.Busy, AgentState.Break);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);
        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Break);
        // The first sweep applied it; the second no longer finds it pending → no new event.
        ctx.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldApplyPerTenantTimeout_WhenTenantsDiffer()
    {
        var ctx = new TestContext();
        await ctx.SetTenantTimeoutAsync(Tenant, timeoutMinutes: 0);    // disabled → never force-applies
        await ctx.SetTenantTimeoutAsync(TenantB, timeoutMinutes: 30);  // enabled → force-applies past 30m
        var stale = ctx.Now - TimeSpan.FromHours(1);
        var agentA = await ctx.SeedPendingAgentAsync(Tenant, AgentState.Busy, AgentState.Break, pendingSince: stale);
        var agentB = await ctx.SeedPendingAgentAsync(TenantB, AgentState.Busy, AgentState.Break, pendingSince: stale);
        await ctx.SeedActiveWorkAsync(Tenant, agentA.AgentId);
        await ctx.SeedActiveWorkAsync(TenantB, agentB.AgentId);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        var a = await ctx.AgentStore.GetByIdAsync(agentA.TenantId, agentA.AgentId, CancellationToken.None);
        var b = await ctx.AgentStore.GetByIdAsync(agentB.TenantId, agentB.AgentId, CancellationToken.None);
        a!.State.Should().Be(AgentState.Busy);    // tenant disabled timeout → still pending
        a.HasPendingPause.Should().BeTrue();
        b!.State.Should().Be(AgentState.Break);   // tenant enabled timeout → force-applied
        b.HasPendingPause.Should().BeFalse();
        ctx.Events.Should().ContainSingle();
        ctx.Events[0].TenantId.Should().Be(TenantB);
    }

    // ─── Test harness ──────────────────────────────────────────────────────────

    private sealed class TestContext
    {
        public InMemoryAgentStore AgentStore { get; }
        public InMemoryConversationStore ConversationStore { get; }
        public InMemoryTenantAuthConfigStore AuthConfigStore { get; }
        public IAuditService Audit { get; }
        public PendingPauseDrainWorker Worker { get; }
        public List<AgentStateChangedEvent> Events { get; } = [];
        public List<AgentPendingStateChangedEvent> PendingEvents { get; } = [];

        private readonly TestClock _clock;

        public DateTimeOffset Now => _clock.GetUtcNow();

        public TestContext(bool isLeader = true)
        {
            _clock = new TestClock(DateTimeOffset.Parse(
                "2026-06-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            AgentStore = new InMemoryAgentStore();
            ConversationStore = new InMemoryConversationStore();
            AuthConfigStore = new InMemoryTenantAuthConfigStore();
            Audit = Substitute.For<IAuditService>();

            var bus = new PlatformEventBus();
            bus.Events.OfType<AgentStateChangedEvent>().Subscribe(Events.Add);
            bus.Events.OfType<AgentPendingStateChangedEvent>().Subscribe(PendingEvents.Add);

            var leader = Substitute.For<IClusterLeader>();
            leader.IsLeader.Returns(isLeader);

            Worker = new PendingPauseDrainWorker(
                AgentStore, ConversationStore, AuthConfigStore, bus, Audit, leader,
                NullLogger<PendingPauseDrainWorker>.Instance, _clock);
        }

        public async Task<Agent> SeedPendingAgentAsync(
            string tenantId, AgentState state, AgentState pendingState, DateTimeOffset? pendingSince = null)
        {
            var agent = new Agent
            {
                AgentId = EntityId.New(),
                TenantId = new TenantId(tenantId),
                UserId = EntityId.New(),
                DisplayName = "Test Agent",
                State = state,
                PendingState = pendingState,
                PendingReason = "lunch",
                PendingSince = pendingSince ?? _clock.GetUtcNow(),
                CreatedAt = _clock.GetUtcNow(),
            };
            await AgentStore.SaveAsync(agent, CancellationToken.None);
            return agent;
        }

        public async Task SeedActiveWorkAsync(string tenantId, EntityId agentId)
        {
            var conversation = new Conversation
            {
                ConversationId = EntityId.New(),
                TenantId = new TenantId(tenantId),
                ContactId = EntityId.New(),
                Channel = ChannelType.WebChat,
                Owner = ConversationOwner.ForAgent(agentId),
                State = ConversationState.Active,
                CreatedAt = _clock.GetUtcNow(),
            };
            await ConversationStore.SaveAsync(conversation, CancellationToken.None);
        }

        public Task SetTenantTimeoutAsync(string tenantId, int timeoutMinutes) =>
            AuthConfigStore.SaveAsync(
                new TenantAuthConfig
                {
                    TenantId = tenantId,
                    PendingPauseTimeoutMinutes = timeoutMinutes,
                },
                CancellationToken.None);
    }

    /// <summary>Tiny ad-hoc replacement for <c>FakeTimeProvider</c> — a fixed clock.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }
}
