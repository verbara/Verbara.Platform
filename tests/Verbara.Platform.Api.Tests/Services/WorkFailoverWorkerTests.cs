using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// W5 — unit tests for the leader-gated work-failover worker. The service is exercised
/// through its public <c>SweepOnceAsync</c> hook so the loop's <see cref="PeriodicTimer"/>
/// never has to fire (deterministic + fast). Real in-memory agent + conversation stores
/// back the failover queries end-to-end; only the switchboard, leader, audit and tenant
/// auth config are substituted so we can assert the re-queue contract.
/// </summary>
public sealed class WorkFailoverWorkerTests
{
    private const string Tenant = "tenant-1";
    private const string OriginQueue = "queue-origin-1";

    [Fact]
    public async Task SweepOnceAsync_ShouldNoOp_WhenNotLeader()
    {
        var ctx = new TestContext(isLeader: false);
        var agent = await ctx.SeedOfflineAgentAsync(Tenant, offlineFor: TimeSpan.FromMinutes(1));
        var conv = await ctx.SeedFailoverWorkAsync(Tenant, agent.AgentId, originQueueId: OriginQueue);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        await ctx.Switchboard.DidNotReceiveWithAnyArgs().RequeueToFrontAsync(default, default, default, default);
        var refreshed = await ctx.ConversationStore.GetByIdAsync(conv.TenantId, conv.ConversationId, CancellationToken.None);
        refreshed!.Metadata.ContainsKey("failoverAttempts").Should().BeFalse();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldNotRequeue_WhenGraceNotElapsed()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedOfflineAgentAsync(Tenant, offlineFor: TimeSpan.FromSeconds(5));   // grace = 30
        await ctx.SeedFailoverWorkAsync(Tenant, agent.AgentId, originQueueId: OriginQueue);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        await ctx.Switchboard.DidNotReceiveWithAnyArgs().RequeueToFrontAsync(default, default, default, default);
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldRequeue_WhenOfflinePastGrace()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedOfflineAgentAsync(Tenant, offlineFor: TimeSpan.FromSeconds(60));   // grace = 30
        var conv = await ctx.SeedFailoverWorkAsync(Tenant, agent.AgentId, originQueueId: OriginQueue);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        await ctx.Switchboard.Received(1).RequeueToFrontAsync(
            Arg.Is<EntityId>(c => c == conv.ConversationId),
            Arg.Is<TenantId>(t => t.Value == Tenant),
            Arg.Is<EntityId>(q => q.Value == OriginQueue),
            Arg.Any<CancellationToken>());

        var refreshed = await ctx.ConversationStore.GetByIdAsync(conv.TenantId, conv.ConversationId, CancellationToken.None);
        refreshed!.Metadata["failoverAttempts"].Should().Be("1");   // counter persisted
        refreshed.Metadata.ContainsKey("failoverStuck").Should().BeFalse();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldEscalateNotRequeue_WhenNoOriginQueue()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedOfflineAgentAsync(Tenant, offlineFor: TimeSpan.FromSeconds(60));
        var conv = await ctx.SeedFailoverWorkAsync(Tenant, agent.AgentId, originQueueId: null);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        await ctx.Switchboard.DidNotReceiveWithAnyArgs().RequeueToFrontAsync(default, default, default, default);
        var refreshed = await ctx.ConversationStore.GetByIdAsync(conv.TenantId, conv.ConversationId, CancellationToken.None);
        refreshed!.Metadata["failoverStuck"].Should().Be("true");

        await ctx.Audit.Received(1).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == Tenant),
            "conversations",
            "conversation.failover.escalated",
            "warning",
            "system",
            "system",
            conv.ConversationId.Value,
            "Conversation",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m["reason"] == "no_origin_queue"),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldEscalate_WhenMaxAttemptsReached()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedOfflineAgentAsync(Tenant, offlineFor: TimeSpan.FromSeconds(60));
        var conv = await ctx.SeedFailoverWorkAsync(
            Tenant, agent.AgentId, originQueueId: OriginQueue, failoverAttempts: 3);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        await ctx.Switchboard.DidNotReceiveWithAnyArgs().RequeueToFrontAsync(default, default, default, default);
        var refreshed = await ctx.ConversationStore.GetByIdAsync(conv.TenantId, conv.ConversationId, CancellationToken.None);
        refreshed!.Metadata["failoverStuck"].Should().Be("true");

        await ctx.Audit.Received(1).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == Tenant),
            "conversations",
            "conversation.failover.escalated",
            "warning",
            "system",
            "system",
            conv.ConversationId.Value,
            "Conversation",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m["reason"] == "max_attempts"),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldSkip_WhenGraceZero()
    {
        var ctx = new TestContext();
        await ctx.SetTenantGraceAsync(Tenant, graceSeconds: 0);   // failover disabled for the tenant
        var agent = await ctx.SeedOfflineAgentAsync(Tenant, offlineFor: TimeSpan.FromHours(1));
        await ctx.SeedFailoverWorkAsync(Tenant, agent.AgentId, originQueueId: OriginQueue);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        await ctx.Switchboard.DidNotReceiveWithAnyArgs().RequeueToFrontAsync(default, default, default, default);
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldSkipAlreadyStuck_WhenFailoverStuckSet()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedOfflineAgentAsync(Tenant, offlineFor: TimeSpan.FromSeconds(60));
        var conv = await ctx.SeedFailoverWorkAsync(
            Tenant, agent.AgentId, originQueueId: OriginQueue, alreadyStuck: true);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        await ctx.Switchboard.DidNotReceiveWithAnyArgs().RequeueToFrontAsync(default, default, default, default);
        ctx.Audit.ReceivedCalls().Should().BeEmpty();   // already escalated → no further audit
        var refreshed = await ctx.ConversationStore.GetByIdAsync(conv.TenantId, conv.ConversationId, CancellationToken.None);
        refreshed!.Metadata.ContainsKey("failoverAttempts").Should().BeFalse();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldIncrementAttempts_OnRequeue()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedOfflineAgentAsync(Tenant, offlineFor: TimeSpan.FromSeconds(60));
        var conv = await ctx.SeedFailoverWorkAsync(Tenant, agent.AgentId, originQueueId: OriginQueue);

        // Pre-condition: no counter yet (0).
        var before = await ctx.ConversationStore.GetByIdAsync(conv.TenantId, conv.ConversationId, CancellationToken.None);
        before!.Metadata.ContainsKey("failoverAttempts").Should().BeFalse();

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        var after = await ctx.ConversationStore.GetByIdAsync(conv.TenantId, conv.ConversationId, CancellationToken.None);
        after!.Metadata["failoverAttempts"].Should().Be("1");   // 0 -> 1
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldAuditRequeued_WhenRequeued()
    {
        var ctx = new TestContext();
        var agent = await ctx.SeedOfflineAgentAsync(Tenant, offlineFor: TimeSpan.FromSeconds(60));
        var conv = await ctx.SeedFailoverWorkAsync(Tenant, agent.AgentId, originQueueId: OriginQueue);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        await ctx.Audit.Received(1).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == Tenant),
            "conversations",
            "conversation.failover.requeued",
            "info",
            "system",
            "system",
            conv.ConversationId.Value,
            "Conversation",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m =>
                m["former_agent"] == agent.AgentId.Value
                && m["origin_queue"] == OriginQueue
                && m["attempt"] == "1"),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    // ─── Test harness ──────────────────────────────────────────────────────────

    private sealed class TestContext
    {
        public InMemoryAgentStore AgentStore { get; }
        public InMemoryConversationStore ConversationStore { get; }
        public InMemoryTenantAuthConfigStore AuthConfigStore { get; }
        public IConversationSwitchboard Switchboard { get; }
        public IAuditService Audit { get; }
        public WorkFailoverWorker Worker { get; }

        private readonly TestClock _clock;

        public DateTimeOffset Now => _clock.GetUtcNow();

        public TestContext(bool isLeader = true)
        {
            _clock = new TestClock(DateTimeOffset.Parse(
                "2026-06-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            AgentStore = new InMemoryAgentStore();
            ConversationStore = new InMemoryConversationStore();
            AuthConfigStore = new InMemoryTenantAuthConfigStore();
            Switchboard = Substitute.For<IConversationSwitchboard>();
            Audit = Substitute.For<IAuditService>();

            // Default switchboard: re-queue succeeds.
            Switchboard.RequeueToFrontAsync(
                    Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                .Returns(new OwnershipResult(true, null, ConversationState.Queued, null));

            var leader = Substitute.For<IClusterLeader>();
            leader.IsLeader.Returns(isLeader);

            Worker = new WorkFailoverWorker(
                AgentStore, ConversationStore, Switchboard, AuthConfigStore, Audit, leader,
                NullLogger<WorkFailoverWorker>.Instance, _clock);
        }

        public async Task<Agent> SeedOfflineAgentAsync(string tenantId, TimeSpan offlineFor)
        {
            var agent = new Agent
            {
                AgentId = EntityId.New(),
                TenantId = new TenantId(tenantId),
                UserId = EntityId.New(),
                DisplayName = "Test Agent",
                State = AgentState.Offline,
                OfflineSince = _clock.GetUtcNow() - offlineFor,
                CreatedAt = _clock.GetUtcNow(),
            };
            await AgentStore.SaveAsync(agent, CancellationToken.None);
            return agent;
        }

        public async Task<Conversation> SeedFailoverWorkAsync(
            string tenantId,
            EntityId agentId,
            string? originQueueId,
            int failoverAttempts = 0,
            bool alreadyStuck = false)
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
            if (!string.IsNullOrWhiteSpace(originQueueId))
                conversation.SetMetadata("originQueueId", originQueueId);
            if (failoverAttempts > 0)
                conversation.SetMetadata("failoverAttempts",
                    failoverAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (alreadyStuck)
                conversation.SetMetadata("failoverStuck", "true");
            await ConversationStore.SaveAsync(conversation, CancellationToken.None);
            return conversation;
        }

        public Task SetTenantGraceAsync(string tenantId, int graceSeconds) =>
            AuthConfigStore.SaveAsync(
                new TenantAuthConfig
                {
                    TenantId = tenantId,
                    WorkFailoverGraceSeconds = graceSeconds,
                },
                CancellationToken.None);
    }

    /// <summary>Tiny ad-hoc replacement for <c>FakeTimeProvider</c> — a fixed clock.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }
}
