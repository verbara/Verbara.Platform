using System.Reactive.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// W3 — unit tests for the leader-gated agent-liveness reaper. The service is
/// exercised through its public <c>SweepOnceAsync</c> hook so the loop's
/// <see cref="PeriodicTimer"/> never has to fire (deterministic + fast).
/// </summary>
public sealed class AgentLivenessReaperTests
{
    private const string Tenant = "tenant-1";
    private const string TenantB = "tenant-2";

    [Fact]
    public async Task SweepOnce_ShouldForceOffline_WhenRoutableAgentHasNoPresenceKey()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedAgentAsync(Tenant, AgentState.Available);

        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Offline);
    }

    [Fact]
    public async Task SweepOnce_ShouldPublishOfflineEvent_WhenAgentReaped()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedAgentAsync(Tenant, AgentState.Busy);

        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);

        ctx.Events.Should().ContainSingle();
        var evt = ctx.Events[0];
        evt.NewState.Should().Be("Offline");
        evt.OldState.Should().Be("Busy");
        evt.AgentId.Should().Be(agent.AgentId.Value);
        evt.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task SweepOnce_ShouldNotReap_WhenAgentHasLivePresenceKey()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedAgentAsync(Tenant, AgentState.Available);

        // The agent's browser heartbeat keeps the presence key alive.
        await ctx.LivenessStore.TouchAsync(
            agent.TenantId, agent.AgentId, TimeSpan.FromSeconds(60), "node-1", CancellationToken.None);

        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Available);
        ctx.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnce_ShouldNotReap_WhenAgentAlreadyNonRoutable()
    {
        var ctx = new TestContext();
        // Break is non-routable → StreamRoutableAgentsAsync never yields it.
        var agent = await ctx.SeedAgentAsync(Tenant, AgentState.Break);

        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Break);
        ctx.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnce_ShouldDoNothing_WhenNotLeader()
    {
        var ctx = new TestContext(isLeader: false);
        var agent = await ctx.SeedAgentAsync(Tenant, AgentState.Available);

        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Available);
        ctx.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnce_ShouldSkipTenant_WhenTimeoutSecondsIsZero()
    {
        var ctx = new TestContext();
        await ctx.SetTenantTimeoutAsync(Tenant, timeoutSeconds: 0);
        var agent = await ctx.SeedAgentAsync(Tenant, AgentState.Available);

        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Available);
        ctx.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnce_ShouldEmitAuditEvent_WhenAgentReaped()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedAgentAsync(Tenant, AgentState.Available);

        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);

        await ctx.Audit.Received(1).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == Tenant),
            "queues",
            "agent.liveness.force_offline",
            "warning",
            "system",
            "system",
            agent.AgentId.Value,
            "Agent",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m =>
                m["old_state"] == "Available"
                && m["reason"] == "missing_presence_key"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnce_ShouldBeIdempotent_WhenRunTwiceOnSameDeadAgent()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedAgentAsync(Tenant, AgentState.Available);

        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);
        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);

        var refreshed = await ctx.AgentStore.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None);
        refreshed!.State.Should().Be(AgentState.Offline);
        // The first sweep reaped it; the second finds it already Offline (non-routable) → no new event.
        ctx.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task SweepOnce_ShouldUsePerTenantTimeout_WhenTenantsDiffer()
    {
        var ctx = new TestContext();
        await ctx.SetTenantTimeoutAsync(Tenant, timeoutSeconds: 0);   // disabled
        await ctx.SetTenantTimeoutAsync(TenantB, timeoutSeconds: 60); // enabled
        var deadA = await ctx.SeedAgentAsync(Tenant, AgentState.Available);
        var deadB = await ctx.SeedAgentAsync(TenantB, AgentState.Available);

        await ctx.Reaper.SweepOnceAsync(CancellationToken.None);

        var a = await ctx.AgentStore.GetByIdAsync(deadA.TenantId, deadA.AgentId, CancellationToken.None);
        var b = await ctx.AgentStore.GetByIdAsync(deadB.TenantId, deadB.AgentId, CancellationToken.None);
        a!.State.Should().Be(AgentState.Available);     // tenant disabled liveness
        b!.State.Should().Be(AgentState.Offline);       // tenant enabled → reaped
        ctx.Events.Should().ContainSingle();
        ctx.Events[0].TenantId.Should().Be(TenantB);
    }

    // ─── Test harness ──────────────────────────────────────────────────────────

    private sealed class TestContext
    {
        public InMemoryAgentStore AgentStore { get; }
        public InMemoryAgentLivenessStore LivenessStore { get; }
        public InMemoryTenantAuthConfigStore AuthConfigStore { get; }
        public IAuditService Audit { get; }
        public AgentLivenessReaper Reaper { get; }
        public List<AgentStateChangedEvent> Events { get; } = [];

        private readonly TestClock _clock;

        public TestContext(bool isLeader = true)
        {
            _clock = new TestClock(DateTimeOffset.Parse(
                "2026-06-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            AgentStore = new InMemoryAgentStore();
            LivenessStore = new InMemoryAgentLivenessStore(_clock);
            AuthConfigStore = new InMemoryTenantAuthConfigStore();
            Audit = Substitute.For<IAuditService>();

            var bus = new PlatformEventBus();
            bus.Events.OfType<AgentStateChangedEvent>().Subscribe(Events.Add);

            var leader = Substitute.For<IClusterLeader>();
            leader.IsLeader.Returns(isLeader);

            Reaper = new AgentLivenessReaper(
                AgentStore, LivenessStore, AuthConfigStore, bus, Audit, leader,
                NullLogger<AgentLivenessReaper>.Instance, _clock);
        }

        public async Task<Agent> SeedAgentAsync(string tenantId, AgentState state)
        {
            var agent = new Agent
            {
                AgentId = EntityId.New(),
                TenantId = new TenantId(tenantId),
                UserId = EntityId.New(),
                DisplayName = "Test Agent",
                State = state,
                CreatedAt = _clock.GetUtcNow(),
            };
            await AgentStore.SaveAsync(agent, CancellationToken.None);
            return agent;
        }

        public Task SetTenantTimeoutAsync(string tenantId, int timeoutSeconds) =>
            AuthConfigStore.SaveAsync(
                new TenantAuthConfig
                {
                    TenantId = tenantId,
                    AgentLivenessTimeoutSeconds = timeoutSeconds,
                },
                CancellationToken.None);
    }

    /// <summary>Tiny ad-hoc replacement for <c>FakeTimeProvider</c> — a fixed clock.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }
}
