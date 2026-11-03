using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Tests.Conversations;
using System.Diagnostics.Metrics;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// conversations-close-after-inactivity task 2.2 (design D3, D4, D6; spec <c>conversation-inactivity-close</c>): the
/// inactivity pass of <see cref="ConversationTimeoutWorker"/>, over the real in-memory store and the real switchboard.
/// The store is reached through a forwarding substitute so a test can count the pass's reads and hold a worker between
/// <c>ListInactiveAsync</c> and <c>TryCloseInactiveAsync</c> (the ADR-0030 widened-window control).
/// </summary>
public sealed partial class ConversationTimeoutWorkerTests
{
    private const string SystemActor = "verbara:system:inactivity-close";

    private static readonly EntityId IdleAgent = EntityId.From("agent-idle");

    [Theory]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.OnHold)]
    [InlineData(ConversationState.Consulting)]
    [InlineData(ConversationState.WaitingForCustomer)]
    [InlineData(ConversationState.Resolved)]
    public async Task InactivityPass_ShouldCloseTheChat_WhenItsStateIsInScopeAndItIsIdlePastTheWindow(ConversationState state)
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(state, idle: TimeSpan.FromHours(73));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        var stored = await h.GetAsync(conv);
        Assert.Equal(ConversationState.Closed, stored.State);
        Assert.Equal(h.Now, stored.ClosedAt);
        Assert.Equal(ConversationCloseReasons.Inactivity, stored.Metadata[ConversationCloseReasons.MetadataKey]);
        Assert.Equal(conv.Owner, stored.Owner); // the owner is kept
        var evt = Assert.Single(h.ClosedEvents);
        Assert.Equal(state.ToString(), evt.OldState);
    }

    [Theory]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.OnHold)]
    [InlineData(ConversationState.Consulting)]
    [InlineData(ConversationState.WaitingForCustomer)]
    [InlineData(ConversationState.Resolved)]
    public async Task InactivityPass_ShouldKeepTheChatOpen_WhenItIsIdleInsideTheWindow(ConversationState state)
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(state, idle: TimeSpan.FromHours(71));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(state, (await h.GetAsync(conv)).State);
        Assert.Empty(h.ClosedEvents);
    }

    [Theory]
    [InlineData(ConversationState.Snoozed)]
    [InlineData(ConversationState.Escalated)]
    [InlineData(ConversationState.WrapUp)]
    [InlineData(ConversationState.Queued)]
    [InlineData(ConversationState.Offered)]
    public async Task InactivityPass_ShouldLeaveTheConversationUntouched_WhenItsStateIsOutOfScope(ConversationState state)
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(state, idle: TimeSpan.FromHours(200));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        var stored = await h.GetAsync(conv);
        Assert.Equal(state, stored.State);
        Assert.False(stored.Metadata.ContainsKey(ConversationCloseReasons.MetadataKey));
        Assert.Empty(h.ClosedEvents);
        Assert.Equal(0, h.CloseAttempts);
    }

    [Fact]
    public async Task InactivityPass_ShouldLeaveTheConversationUntouched_WhenItIsVoice()
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(200), channel: ChannelType.Voice);

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Active, (await h.GetAsync(conv)).State);
        Assert.Empty(h.ClosedEvents);
        await h.Capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task InactivityPass_ShouldKeepTheChatOpen_WhenTheAgentRepliedTenHoursAgo()
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        // D9: the agent's reply is claimed (clock advanced to the send time) before it is stored.
        Assert.True(await h.Inner.TryClaimForSendAsync(h.Tenant, conv.ConversationId, h.Now.AddHours(-10), advanceClock: true, CancellationToken.None));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Active, (await h.GetAsync(conv)).State);
        Assert.Empty(h.ClosedEvents);
    }

    [Fact]
    public async Task InactivityPass_ShouldCloseTheChat_WhenOnlyAnInternalNoteWasAddedAnHourAgo()
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        // A note is a non-activity message, and touching the row (a full-row save) never moves the clock (D2).
        await h.Messages.SaveAsync(new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conv.ConversationId,
            TenantId = h.Tenant,
            Direction = MessageDirection.System,
            Channel = ChannelType.WebChat,
            Content = new MessageEnvelope([new TextBlock("supervisor note")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = h.Now.AddHours(-1),
        }, CancellationToken.None);
        var touched = await h.GetAsync(conv);
        touched.UpdatedAt = h.Now.AddHours(-1);
        await h.Inner.SaveAsync(touched, CancellationToken.None);

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Closed, (await h.GetAsync(conv)).State);
    }

    [Fact]
    public async Task InactivityPass_ShouldCloseTheChat_WhenItWasPutOnHoldAnHourAgo()
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        // The hold: a state change saved through the full-row save an hour ago. State changes never move the clock.
        var held = await h.GetAsync(conv);
        held.TransitionTo(ConversationState.OnHold, h.Now.AddHours(-1));
        held.UpdatedAt = h.Now.AddHours(-1);
        await h.Inner.SaveAsync(held, CancellationToken.None);

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Closed, (await h.GetAsync(conv)).State);
        Assert.Equal(nameof(ConversationState.OnHold), Assert.Single(h.ClosedEvents).OldState);
    }

    [Fact]
    public async Task InactivityPass_ShouldCountFromCreation_WhenTheChatHoldsNoMessage()
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(73), withClock: false);

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Closed, (await h.GetAsync(conv)).State);
    }

    [Fact]
    public async Task InactivityPass_ShouldCloseNothing_WhenTheSwitchIsOff()
    {
        using var h = new InactivityHarness();
        h.Options.InactivityCloseEnabled = false;
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(200));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Active, (await h.GetAsync(conv)).State);
        Assert.Equal(0, h.ListInactiveCalls);
    }

    [Fact]
    public async Task InactivityPass_ShouldApplyTheHostWindow_WhenItIs24Hours()
    {
        using var h = new InactivityHarness();
        h.Options.InactivityCloseAfterHours = 24;
        var idle25 = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(25));
        var idle23 = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(23));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Closed, (await h.GetAsync(idle25)).State);
        Assert.Equal(ConversationState.Active, (await h.GetAsync(idle23)).State);
    }

    [Fact]
    public async Task InactivityPass_ShouldRunAtMostOncePerInterval_WhenTheWorkerTicksEveryFiveSeconds()
    {
        using var h = new InactivityHarness();
        var worker = h.CreateWorker();
        var start = h.Now;

        await worker.ProcessTimeoutsAsync(CancellationToken.None);
        Assert.Equal(1, h.ListInactiveCalls);

        for (var s = 5; s < 60; s += 5)
        {
            h.Now = start.AddSeconds(s);
            await worker.ProcessTimeoutsAsync(CancellationToken.None);
        }
        Assert.Equal(1, h.ListInactiveCalls);

        h.Now = start.AddSeconds(60);
        await worker.ProcessTimeoutsAsync(CancellationToken.None);
        Assert.Equal(2, h.ListInactiveCalls);
    }

    [Fact]
    public async Task InactivityPass_ShouldAuditTheCloseOnceWithTheSystemActor_WhenItClosesAChat()
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(72.5));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        await h.Audit.Received(1).RecordAsync(
            h.Tenant, "conversations", "conversation.closed", "info", SystemActor, "system",
            conv.ConversationId.Value, "Conversation", Arg.Any<Guid?>(), Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(m => m != null && m["reason"] == ConversationCloseReasons.Inactivity),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, h.ClosedAuditCount);
        await h.Capacity.Received(1).ReleaseAsync(h.Tenant, IdleAgent, ChannelType.WebChat, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InactivityPass_ShouldKeepTheChatOpen_WhenAMessageIsStoredBetweenListAndClose()
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        // D7: the inbound claim commits after the pass listed the chat and before its conditional close.
        h.AfterList = () => h.Inner.TryClaimForInboundAsync(h.Tenant, conv.ConversationId, h.Now, CancellationToken.None);

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(1, h.CloseAttempts);
        Assert.Equal(ConversationState.Active, (await h.GetAsync(conv)).State);
        Assert.Empty(h.ClosedEvents);
        Assert.Equal(0, h.ClosedAuditCount);
        await h.Capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task InactivityPass_ShouldCloseOnceWithOneEventAuditAndRelease_WhenTwoWorkersSweepOneStore()
    {
        var result = await RunTwoWorkersAsync(unconditionalClose: false);

        Assert.Equal(ConversationState.Closed, result.FinalState);
        Assert.Equal(2, result.CloseAttempts); // both workers reached the close: the window was held open
        Assert.Equal(1, result.Events);
        Assert.Equal(1, result.Audits);
        Assert.Equal(1, result.Releases);
    }

    /// <summary>
    /// ADR-0030 M-load control for the test above: the same held window, but the close is the gate-OFF wrap-up shape
    /// (load, set <c>Closed</c>, unconditional full-row <c>SaveAsync</c>). It must double-close, proving the hold really
    /// puts both workers inside the window; if it ever stops double-closing, the candidate's pass proves nothing.
    /// </summary>
    [Fact]
    public async Task InactivityPass_ShouldDoubleClose_WhenTheCloseIsAnUnconditionalSave_Control()
    {
        var result = await RunTwoWorkersAsync(unconditionalClose: true);

        Assert.Equal(2, result.CloseAttempts);
        Assert.Equal(2, result.Events);
        Assert.Equal(2, result.Audits);
        Assert.Equal(2, result.Releases);
    }

    internal sealed record TwoWorkerResult(
        ConversationState FinalState, int CloseAttempts, int Events, int Audits, int Releases,
        IReadOnlyDictionary<string, long> Counted, IReadOnlyList<string> CloseLogs);

    internal static async Task<TwoWorkerResult> RunTwoWorkersAsync(bool unconditionalClose, TimeSpan? idle = null)
    {
        using var h = new InactivityHarness { UnconditionalClose = unconditionalClose };
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        var conv = await h.SeedAsync(ConversationState.Active, idle: idle ?? TimeSpan.FromHours(80));

        // The injected delay point: each worker is held after ListInactiveAsync returns and before
        // TryCloseInactiveAsync, released only when both have listed.
        var listed = 0;
        var bothListed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.AfterList = async () =>
        {
            if (Interlocked.Increment(ref listed) == 2)
                bothListed.TrySetResult();
            await bothListed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var a = h.CreateWorker();
        var b = h.CreateWorker();
        await Task.WhenAll(
            Task.Run(() => a.ProcessTimeoutsAsync(CancellationToken.None)),
            Task.Run(() => b.ProcessTimeoutsAsync(CancellationToken.None)));

        var releases = h.Capacity.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAgentCapacityService.ReleaseAsync));
        return new TwoWorkerResult(
            (await h.GetAsync(conv)).State, h.CloseAttempts, h.ClosedEvents.Count, h.ClosedAuditCount, releases,
            h.CountedByReason, CloseLogsOf(logger));
    }

    /// <summary>The in-memory store, switchboard, event bus and options one inactivity test needs.</summary>
    internal sealed class InactivityHarness : IDisposable
    {
        private readonly List<ConversationStateChangedEvent> _closed = [];
        private readonly IDisposable _subscription;
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _counted = new(StringComparer.Ordinal);
        private readonly List<string> _passCalls = [];
        private int _listInactiveCalls;
        private int _closeAttempts;

        public InactivityHarness()
        {
            Inner = new InMemoryConversationStore(Messages);
            Clock.UtcNow.Returns(_ => Now);
            TenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Tenants.Select(id => new Tenant { TenantId = id, Name = id, Status = TenantStatus.Active }).ToArray());
            AccountStatus.GetActiveUserIdsAsync(Arg.Any<TenantId>(), Arg.Any<IReadOnlyCollection<EntityId>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IReadOnlySet<EntityId>>(new HashSet<EntityId>(call.Arg<IReadOnlyCollection<EntityId>>())));

            // Forward what the worker and the switchboard use to the real in-memory store.
            Store.ListByStateAsync(Arg.Any<TenantId>(), Arg.Any<ConversationState>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(c => Inner.ListByStateAsync(c.ArgAt<TenantId>(0), c.ArgAt<ConversationState>(1), c.ArgAt<int>(2), c.ArgAt<CancellationToken>(3)));
            Store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                .Returns(c => Inner.GetByIdAsync(c.ArgAt<TenantId>(0), c.ArgAt<EntityId>(1), c.ArgAt<CancellationToken>(2)));
            Store.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())
                .Returns(c => Inner.SaveAsync(c.ArgAt<Conversation>(0), c.ArgAt<CancellationToken>(1)));
            Store.ListInactiveAsync(Arg.Any<TenantId>(), Arg.Any<IReadOnlyCollection<ConversationState>>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(c => ListThenHoldAsync(c.ArgAt<TenantId>(0), c.ArgAt<IReadOnlyCollection<ConversationState>>(1), c.ArgAt<DateTimeOffset>(2), c.ArgAt<int>(3), c.ArgAt<CancellationToken>(4)));
            Store.TryCloseInactiveAsync(Arg.Any<TenantId>(), Arg.Any<InactivityCloseRequest>(), Arg.Any<CancellationToken>())
                .Returns(c => CloseAsync(c.ArgAt<TenantId>(0), c.ArgAt<InactivityCloseRequest>(1), c.ArgAt<CancellationToken>(2)));
            Store.ReconcileLastMessageAtAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
                .Returns(c => ReconcileAsync(c.ArgAt<TenantId>(0), c.ArgAt<CancellationToken>(1)));

            Switchboard = new ConversationSwitchboard(Store, Capacity, Substitute.For<IAgentStore>(), Clock, EventBus, AccountStatus);

            _subscription = EventBus.Events.Subscribe(e =>
            {
                if (e is ConversationStateChangedEvent { NewState: nameof(ConversationState.Closed) } sc)
                    lock (_closed) _closed.Add(sc);
            });

            // D13: the close counter of this harness only (other tests' meters share the name).
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, Metrics.SystemClosed))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag is { Key: "reason", Value: string reason })
                    {
                        lock (_counted)
                            _counted[reason] = _counted.GetValueOrDefault(reason) + value;
                    }
                }
            });
            _listener.Start();
        }

        /// <summary>The close counter the worker increments after a won close (D13).</summary>
        public ConversationCloseMetrics Metrics { get; } = new();

        /// <summary>What the close counter received, by its <c>reason</c> tag.</summary>
        public IReadOnlyDictionary<string, long> CountedByReason
        {
            get { lock (_counted) return new Dictionary<string, long>(_counted, StringComparer.Ordinal); }
        }

        public TenantId Tenant { get; } = new(TestTenantId);

        /// <summary>The active tenants the worker sweeps, in order (2.6 adds a second one).</summary>
        public List<string> Tenants { get; } = [TestTenantId];

        /// <summary>The tenant-auth-config store the worker reads the per-tenant window from (2.6, D11); none by default.</summary>
        public Verbara.Platform.Identity.ITenantAuthConfigStore? TenantAuthConfig { get; set; }

        public Microsoft.Extensions.Logging.ILogger<ConversationTimeoutWorker> Logger { get; set; } =
            NullLogger<ConversationTimeoutWorker>.Instance;
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        public InMemoryMessageStore Messages { get; } = new();
        public InMemoryConversationStore Inner { get; }
        public IConversationStore Store { get; } = Substitute.For<IConversationStore>();
        public ITenantStore TenantStore { get; } = Substitute.For<ITenantStore>();
        public IAgentCapacityService Capacity { get; } = Substitute.For<IAgentCapacityService>();
        public IAgentAccountStatusLookup AccountStatus { get; } = Substitute.For<IAgentAccountStatusLookup>();
        public IAuditService Audit { get; } = Substitute.For<IAuditService>();
        public IClock Clock { get; } = Substitute.For<IClock>();
        public PlatformEventBus EventBus { get; } = new();
        public ConversationSwitchboard Switchboard { get; }
        public ConversationOptions Options { get; } = new();

        // The queue and wrap-up timeouts are pushed out of the way so a 200 h row in their states is left to them.
        public DistributionOptions Distribution { get; } = new()
        {
            DefaultQueueTimeoutSeconds = 10_000_000,
            DefaultWrapUpTimeoutSeconds = 10_000_000,
        };

        /// <summary>Runs after <c>ListInactiveAsync</c> returned and before the worker sees the rows.</summary>
        public Func<Task>? AfterList { get; set; }

        /// <summary>Runs inside <c>ReconcileLastMessageAtAsync</c>, before the real reconcile (2.8: a fault injection).</summary>
        public Func<TenantId, Task>? BeforeReconcile { get; set; }

        /// <summary>The pass's reconcile and list calls in order, as <c>reconcile:&lt;tenant&gt;</c> / <c>list:&lt;tenant&gt;</c> (2.8).</summary>
        public IReadOnlyList<string> PassCalls
        {
            get { lock (_passCalls) return [.. _passCalls]; }
        }

        /// <summary>The baseline: the close is a load, set <c>Closed</c>, unconditional full-row save.</summary>
        public bool UnconditionalClose { get; init; }

        public int ListInactiveCalls => Volatile.Read(ref _listInactiveCalls);
        public int CloseAttempts => Volatile.Read(ref _closeAttempts);

        public IReadOnlyList<ConversationStateChangedEvent> ClosedEvents
        {
            get { lock (_closed) return [.. _closed]; }
        }

        public int ClosedAuditCount => Audit.ReceivedCalls().Count(c =>
            c.GetMethodInfo().Name == nameof(IAuditService.RecordAsync)
            && c.GetArguments().Length > 2 && c.GetArguments()[2] is "conversation.closed");

        public ConversationTimeoutWorker CreateWorker() => new(
            Store, TenantStore, Switchboard, EventBus, Clock, new ServiceHeartbeat(),
            Microsoft.Extensions.Options.Options.Create(Distribution),
            Logger,
            auditService: Audit,
            conversationOptions: Microsoft.Extensions.Options.Options.Create(Options),
            tenantAuthConfigStore: TenantAuthConfig,
            closeMetrics: Metrics);

        public async Task<Conversation> SeedAsync(
            ConversationState state, TimeSpan idle, ChannelType channel = ChannelType.WebChat, bool withClock = true,
            TenantId? tenant = null)
        {
            var conv = new Conversation
            {
                ConversationId = EntityId.New(),
                TenantId = tenant ?? Tenant,
                ContactId = EntityId.New(),
                Channel = channel,
                Owner = state is ConversationState.Queued
                    ? ConversationOwner.ForQueue(EntityId.From("q1"))
                    : ConversationOwner.ForAgent(IdleAgent),
                State = state,
                CreatedAt = withClock ? Now - idle - TimeSpan.FromHours(1) : Now - idle,
                UpdatedAt = Now - idle,
                LastMessageAt = withClock ? Now - idle : null,
            };
            await Inner.SaveAsync(conv, CancellationToken.None);
            return conv;
        }

        public async Task<Conversation> GetAsync(Conversation conv) =>
            await Inner.GetByIdAsync(conv.TenantId, conv.ConversationId, CancellationToken.None)
            ?? throw new InvalidOperationException("seeded conversation vanished");

        private async Task<IReadOnlyList<Conversation>> ListThenHoldAsync(
            TenantId tenant, IReadOnlyCollection<ConversationState> states, DateTimeOffset cutoff, int limit, CancellationToken ct)
        {
            Interlocked.Increment(ref _listInactiveCalls);
            lock (_passCalls) _passCalls.Add($"list:{tenant.Value}");
            // Copies, as the Postgres store materializes rows: the in-memory store hands out its live entities, which a
            // concurrent close would mutate under the other worker's feet.
            var rows = (await Inner.ListInactiveAsync(tenant, states, cutoff, limit, ct)).Select(Snapshot).ToList();
            if (AfterList is { } hold)
                await hold();
            return rows;
        }

        private async Task<int> ReconcileAsync(TenantId tenant, CancellationToken ct)
        {
            lock (_passCalls) _passCalls.Add($"reconcile:{tenant.Value}");
            if (BeforeReconcile is { } hook)
                await hook(tenant);
            return await Inner.ReconcileLastMessageAtAsync(tenant, ct);
        }

        private static Conversation Snapshot(Conversation c) => new()
        {
            ConversationId = c.ConversationId,
            TenantId = c.TenantId,
            ContactId = c.ContactId,
            Channel = c.Channel,
            Owner = c.Owner,
            State = c.State,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            ClosedAt = c.ClosedAt,
            LastMessageAt = c.LastMessageAt,
        };

        private async Task<InactivityCloseOutcome?> CloseAsync(TenantId tenant, InactivityCloseRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _closeAttempts);
            if (!UnconditionalClose)
                return await Inner.TryCloseInactiveAsync(tenant, request, ct);

            var row = await Inner.GetByIdAsync(tenant, request.ConversationId, ct);
            if (row is null)
                return null;
            var clock = row.LastMessageAt ?? row.CreatedAt;
            // The reason is decided as the statement does (D12); only the state/owner/clock condition is dropped.
            var reason = clock < request.BacklogCutoff
                ? ConversationCloseReasons.InactivityBacklog
                : ConversationCloseReasons.Inactivity;
            row.State = ConversationState.Closed;
            row.ClosedAt = request.Now;
            row.UpdatedAt = request.Now;
            row.SetMetadata(ConversationCloseReasons.MetadataKey, reason);
            await Inner.SaveAsync(row, ct);
            return new InactivityCloseOutcome(reason, clock);
        }

        public void Dispose()
        {
            _subscription.Dispose();
            _listener.Dispose();
            Metrics.Dispose();
            EventBus.Dispose();
        }
    }
}
