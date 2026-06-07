using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// W5b — unit tests for the leader-gated voice callback-rescue worker. Driven through the
/// public <c>SweepOnceAsync</c> hook so the loop's <see cref="PeriodicTimer"/> never fires
/// (deterministic + fast). A substituted <see cref="IConversationStore"/> gives precise
/// control over the list snapshot vs the per-conversation re-load (idempotency), while real
/// in-memory agent + liveness + tenant-auth-config stores back the worthiness checks. The
/// originator is a hand-rolled recording fake (it is an internal Api interface, not
/// DynamicProxy-visible), and audit + leader are substituted, so we can assert the
/// originate + clear + escalate contract.
/// </summary>
public sealed class CallbackRescueWorkerTests
{
    private const string Tenant = "tenant-1";
    private const string OriginQueue = "queue-origin-1";
    private const string Number = "+15551230000";

    [Fact]
    public async Task SweepOnceAsync_ShouldNoOp_WhenNotLeader()
    {
        var ctx = new TestContext(isLeader: false);
        var conv = ctx.SeedPending(agentLegAbnormal: true, evalSince: ctx.Now - TimeSpan.FromMinutes(1));

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        ctx.Originator.Calls.Should().BeEmpty();
        conv.Metadata.ContainsKey("pendingCallbackEval").Should().BeTrue();   // untouched
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldNotCallback_WhenWithinGrace()
    {
        var ctx = new TestContext();   // grace = 25
        ctx.SeedPending(agentLegAbnormal: true, evalSince: ctx.Now - TimeSpan.FromSeconds(10));

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        ctx.Originator.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldOriginateCallback_WhenAbnormalAndGraceElapsed()
    {
        var ctx = new TestContext();
        var conv = ctx.SeedPending(agentLegAbnormal: true, evalSince: ctx.Now - TimeSpan.FromSeconds(60));

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        ctx.Originator.Calls.Should().ContainSingle();
        var call = ctx.Originator.Calls[0];
        call.TenantId.Value.Should().Be(Tenant);
        call.Number.Should().Be(Number);
        call.OriginQueueId.Value.Should().Be(OriginQueue);
        call.RescuedFrom.Should().Be(conv.ConversationId);
        call.CallbackAttempts.Should().Be(1);

        conv.Metadata.ContainsKey("pendingCallbackEval").Should().BeFalse();   // cleared
        conv.Metadata["callbackEnqueued"].Should().Be("true");
        conv.Metadata["callbackAttempts"].Should().Be("1");
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldNotCallback_WhenCleanEndAndAgentAlive()
    {
        var ctx = new TestContext();
        var conv = ctx.SeedPending(agentLegAbnormal: false, evalSince: ctx.Now - TimeSpan.FromSeconds(60));
        ctx.MarkAgentAlive(conv);   // owning agent confirmed alive, not Offline

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        ctx.Originator.Calls.Should().BeEmpty();
        conv.Metadata.ContainsKey("pendingCallbackEval").Should().BeFalse();   // false-positive → cleared
        conv.Metadata.ContainsKey("callbackEnqueued").Should().BeFalse();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldOriginateCallback_WhenAgentNotAlive()
    {
        var ctx = new TestContext();
        var conv = ctx.SeedPending(agentLegAbnormal: false, evalSince: ctx.Now - TimeSpan.FromSeconds(60));
        // Clean end but the owning agent is NOT alive (liveness backstop) → worthy.
        ctx.SeedOwningAgent(conv, AgentState.Available, alive: false);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        ctx.Originator.Calls.Should().ContainSingle();
        var call = ctx.Originator.Calls[0];
        call.TenantId.Value.Should().Be(Tenant);
        call.Number.Should().Be(Number);
        call.OriginQueueId.Value.Should().Be(OriginQueue);
        call.RescuedFrom.Should().Be(conv.ConversationId);
        call.CallbackAttempts.Should().Be(1);
        conv.Metadata["callbackEnqueued"].Should().Be("true");
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldEscalate_WhenAttemptsExhausted()
    {
        var ctx = new TestContext();
        var conv = ctx.SeedPending(
            agentLegAbnormal: true, evalSince: ctx.Now - TimeSpan.FromSeconds(60), callbackAttempts: 3);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        ctx.Originator.Calls.Should().BeEmpty();
        conv.Metadata["callbackStuck"].Should().Be("true");
        conv.Metadata.ContainsKey("pendingCallbackEval").Should().BeFalse();
        await ctx.AssertEscalatedAsync(conv, reason: "max_attempts");
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldEscalate_WhenNoNumberOrQueue()
    {
        var ctx = new TestContext();
        var conv = ctx.SeedPending(
            agentLegAbnormal: true, evalSince: ctx.Now - TimeSpan.FromSeconds(60), includeNumber: false);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        ctx.Originator.Calls.Should().BeEmpty();
        conv.Metadata["callbackStuck"].Should().Be("true");
        conv.Metadata.ContainsKey("pendingCallbackEval").Should().BeFalse();
        await ctx.AssertEscalatedAsync(conv, reason: "no_number_or_queue");
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldClearPending_WhenGraceDisabled()
    {
        var ctx = new TestContext();
        ctx.SetTenantGrace(0);   // voice rescue disabled for the tenant
        var conv = ctx.SeedPending(agentLegAbnormal: true, evalSince: ctx.Now - TimeSpan.FromHours(1));

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        ctx.Originator.Calls.Should().BeEmpty();
        conv.Metadata.ContainsKey("pendingCallbackEval").Should().BeFalse();
        conv.Metadata.ContainsKey("callbackStuck").Should().BeFalse();
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldIncrementAttemptsBeforeOriginate_AndLeavePendingOnFailure()
    {
        var ctx = new TestContext();
        ctx.Originator.NextResult = false;   // originate not accepted
        var conv = ctx.SeedPending(agentLegAbnormal: true, evalSince: ctx.Now - TimeSpan.FromSeconds(60));

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        conv.Metadata["callbackAttempts"].Should().Be("1");                   // counter persisted BEFORE originate
        conv.Metadata.ContainsKey("pendingCallbackEval").Should().BeTrue();   // left for retry (attempts < max)
        conv.Metadata.ContainsKey("callbackStuck").Should().BeFalse();
        await ctx.AssertFailedAsync(conv, attempt: 1);
    }

    [Fact]
    public async Task SweepOnceAsync_ShouldBeIdempotent_WhenReloadedConversationNoLongerPending()
    {
        var ctx = new TestContext();
        // The list snapshot still flags it pending, but the re-load shows it was already cleared
        // (another pod / a raced state change acted first).
        var snapshot = ctx.MakePending(agentLegAbnormal: true, evalSince: ctx.Now - TimeSpan.FromSeconds(60));
        var reloaded = ctx.MakePending(agentLegAbnormal: true, evalSince: ctx.Now - TimeSpan.FromSeconds(60),
            conversationId: snapshot.ConversationId);
        reloaded.RemoveMetadata("pendingCallbackEval");

        ctx.Conversations.ListPendingCallbackEvalAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Conversation> { snapshot });
        ctx.Conversations.GetByIdAsync(Arg.Any<TenantId>(), snapshot.ConversationId, Arg.Any<CancellationToken>())
            .Returns(reloaded);

        await ctx.Worker.SweepOnceAsync(CancellationToken.None);

        ctx.Originator.Calls.Should().BeEmpty();
    }

    // ─── Test harness ──────────────────────────────────────────────────────────

    private sealed class TestContext
    {
        public IConversationStore Conversations { get; }
        public FakeCallbackOriginator Originator { get; }
        public InMemoryTenantAuthConfigStore AuthConfigStore { get; }
        public InMemoryAgentLivenessStore Liveness { get; }
        public InMemoryAgentStore Agents { get; }
        public IAuditService Audit { get; }
        public PlatformEventBus EventBus { get; }
        public CallbackRescueWorker Worker { get; }

        private readonly TestClock _clock;
        private readonly Dictionary<string, Conversation> _store = new(StringComparer.Ordinal);

        public DateTimeOffset Now => _clock.GetUtcNow();

        public TestContext(bool isLeader = true)
        {
            _clock = new TestClock(DateTimeOffset.Parse(
                "2026-06-06T10:00:00Z", CultureInfo.InvariantCulture));
            Conversations = Substitute.For<IConversationStore>();
            Originator = new FakeCallbackOriginator();
            AuthConfigStore = new InMemoryTenantAuthConfigStore();
            Liveness = new InMemoryAgentLivenessStore(_clock);
            Agents = new InMemoryAgentStore();
            Audit = Substitute.For<IAuditService>();
            EventBus = new PlatformEventBus();

            // The substituted store is backed by an in-memory dictionary so re-load + save round-trips.
            Conversations.ListPendingCallbackEvalAsync(Arg.Any<CancellationToken>())
                .Returns(_ => (IReadOnlyList<Conversation>)_store.Values
                    .Where(c => c.State == ConversationState.WrapUp
                        && c.Metadata.TryGetValue("pendingCallbackEval", out var v) && v == "true")
                    .ToList());
            Conversations.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                .Returns(call => _store.TryGetValue(call.ArgAt<EntityId>(1).Value, out var c) ? c : null);
            Conversations.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var c = call.ArgAt<Conversation>(0);
                    _store[c.ConversationId.Value] = c;
                    return Task.CompletedTask;
                });

            var leader = Substitute.For<IClusterLeader>();
            leader.IsLeader.Returns(isLeader);

            Worker = new CallbackRescueWorker(
                Conversations, Originator, AuthConfigStore, Liveness, Agents, Audit, EventBus, leader,
                NullLogger<CallbackRescueWorker>.Instance, _clock);
        }

        /// <summary>Builds a WrapUp pending-callback-eval conversation WITHOUT persisting it.</summary>
        public Conversation MakePending(
            bool agentLegAbnormal,
            DateTimeOffset evalSince,
            int callbackAttempts = 0,
            bool includeNumber = true,
            bool includeQueue = true,
            EntityId? conversationId = null,
            EntityId? owner = null)
        {
            var conv = new Conversation
            {
                ConversationId = conversationId ?? EntityId.New(),
                TenantId = new TenantId(Tenant),
                ContactId = EntityId.New(),
                Channel = ChannelType.Voice,
                Owner = ConversationOwner.ForAgent(owner ?? EntityId.New()),
                State = ConversationState.WrapUp,
                CreatedAt = _clock.GetUtcNow(),
            };
            conv.SetMetadata("pendingCallbackEval", "true");
            conv.SetMetadata("agentLegAbnormal", agentLegAbnormal ? "true" : "false");
            conv.SetMetadata("callbackEvalSince", evalSince.ToString("O", CultureInfo.InvariantCulture));
            if (includeNumber)
                conv.SetMetadata("callbackNumber", Number);
            if (includeQueue)
                conv.SetMetadata("originQueueId", OriginQueue);
            if (callbackAttempts > 0)
                conv.SetMetadata("callbackAttempts", callbackAttempts.ToString(CultureInfo.InvariantCulture));
            return conv;
        }

        /// <summary>Builds + persists a WrapUp pending conversation into the backing store.</summary>
        public Conversation SeedPending(
            bool agentLegAbnormal,
            DateTimeOffset evalSince,
            int callbackAttempts = 0,
            bool includeNumber = true,
            bool includeQueue = true)
        {
            var conv = MakePending(agentLegAbnormal, evalSince, callbackAttempts, includeNumber, includeQueue);
            _store[conv.ConversationId.Value] = conv;
            return conv;
        }

        /// <summary>Registers the conversation's owning agent with the given state + liveness.</summary>
        public void SeedOwningAgent(Conversation conv, AgentState state, bool alive)
        {
            var agentId = conv.Owner!.OwnerId!.Value;
            var agent = new Agent
            {
                AgentId = agentId,
                TenantId = conv.TenantId,
                UserId = EntityId.New(),
                DisplayName = "Owner",
                State = state,
                CreatedAt = _clock.GetUtcNow(),
            };
            Agents.SaveAsync(agent, CancellationToken.None).GetAwaiter().GetResult();
            if (alive)
                Liveness.TouchAsync(conv.TenantId, agentId, TimeSpan.FromMinutes(1), "node-1", CancellationToken.None)
                    .GetAwaiter().GetResult();
        }

        public void MarkAgentAlive(Conversation conv) => SeedOwningAgent(conv, AgentState.Available, alive: true);

        public void SetTenantGrace(int graceSeconds) =>
            AuthConfigStore.SaveAsync(
                new TenantAuthConfig { TenantId = Tenant, VoiceCallbackGraceSeconds = graceSeconds },
                CancellationToken.None).GetAwaiter().GetResult();

        public async Task AssertEscalatedAsync(Conversation conv, string reason) =>
            await Audit.Received(1).RecordAsync(
                Arg.Is<TenantId>(t => t.Value == Tenant),
                "conversations",
                "conversation.callback.escalated",
                "warning",
                "system",
                "system",
                conv.ConversationId.Value,
                "Conversation",
                Arg.Any<Guid?>(),
                Arg.Any<AuditChanges?>(),
                Arg.Is<IReadOnlyDictionary<string, string>>(m => m["reason"] == reason),
                Arg.Any<CancellationToken>());

        public async Task AssertFailedAsync(Conversation conv, int attempt) =>
            await Audit.Received(1).RecordAsync(
                Arg.Is<TenantId>(t => t.Value == Tenant),
                "conversations",
                "conversation.callback.failed",
                "warning",
                "system",
                "system",
                conv.ConversationId.Value,
                "Conversation",
                Arg.Any<Guid?>(),
                Arg.Any<AuditChanges?>(),
                Arg.Is<IReadOnlyDictionary<string, string>>(m =>
                    m["attempt"] == attempt.ToString(CultureInfo.InvariantCulture)),
                Arg.Any<CancellationToken>());
    }

    /// <summary>Tiny ad-hoc replacement for <c>FakeTimeProvider</c> — a fixed clock.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }

    /// <summary>
    /// Hand-rolled recording fake for the internal <see cref="ICallbackOriginator"/> (it is not
    /// DynamicProxy-visible from the test assembly, so NSubstitute cannot proxy it). Records each
    /// originate call and returns <see cref="NextResult"/> (default <c>true</c> = accepted).
    /// </summary>
    private sealed class FakeCallbackOriginator : ICallbackOriginator
    {
        public bool NextResult { get; set; } = true;
        public List<OriginateCall> Calls { get; } = [];

        public Task<bool> OriginateCallbackAsync(
            TenantId tenantId, string number, EntityId originQueueId, EntityId rescuedFrom, int callbackAttempts, CancellationToken ct)
        {
            Calls.Add(new OriginateCall(tenantId, number, originQueueId, rescuedFrom, callbackAttempts));
            return Task.FromResult(NextResult);
        }

        public sealed record OriginateCall(
            TenantId TenantId, string Number, EntityId OriginQueueId, EntityId RescuedFrom, int CallbackAttempts);
    }
}
