using System.Collections.Concurrent;

using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Workers;

/// <summary>
/// openspec change <c>fix-local-kind-datetimeoffset</c>, task 5.6 — spec scenario
/// "The background distribution loop does not fail on a non-UTC host".
/// <para>
/// <b>Scope — what these tests guard and what they explicitly do NOT.</b> The reported symptom was
/// <see cref="QueueDistributionWorker"/> logging <c>Distribution cycle failed</c> on every tick for
/// the whole process lifetime on a UTC-5 host. The causal chain was: Npgsql's LEGACY timestamp
/// converter handed <c>PostgresConversationStore</c> a <c>DateTime</c> with <c>Kind == Local</c>,
/// the row projection's <c>new DateTimeOffset(value, TimeSpan.Zero)</c> threw
/// <see cref="ArgumentException"/>, <c>DistributeAsync</c> propagated it, and the worker's inner
/// catch logged. Only the last two hops of that chain are reachable from this assembly:
/// <c>Api.Tests</c> lives in the REQUIRED container-free CI lane, so every store here is an
/// NSubstitute double — there is no Npgsql connection, no converter selection and no row projection
/// anywhere on the cycle path.
/// </para>
/// <para>
/// <b>These tests would therefore NOT fail if <c>Npgsql.EnableLegacyTimestampBehavior</c> were
/// reinstated</b> (change task 5.7). They are not a timezone regression test and must not be read
/// as one. The projection layer — the actual defect — is covered by the container-backed
/// Storage.Postgres round-trip coverage (tasks 5.4/5.5) and by the <c>check-endpoint-invariants.py</c>
/// gate that bans the switch outright (design D4).
/// </para>
/// <para>
/// What they DO pin, which nothing else covered: (1) a fully-wired SUCCESSFUL distribution cycle
/// emits zero <c>Distribution cycle failed</c> records; (2) when a cycle does throw, the worker logs
/// exactly once per failing cycle, keeps ticking, and stops logging the moment the underlying cause
/// clears — the "the failure does not recur every cycle for the process lifetime" half of the
/// scenario, which no existing test covered. Test (2) is also the positive control for test (1): it
/// proves the capture matches the real emitted message text, so test (1)'s zero-count assertion
/// cannot pass vacuously through a message-text mismatch.
/// </para>
/// <para>
/// No process-wide <c>TZ</c> / <see cref="TimeZoneInfo"/> mutation is performed, deliberately.
/// <c>src/</c> contains zero reads of <c>DateTime.Now</c>, <c>DateTimeOffset.Now</c>,
/// <c>TimeZoneInfo.Local</c> or <c>ToLocalTime()</c>, so the process timezone is not observable
/// anywhere on this path; the only mechanism that ever produced a <c>Local</c> kind was the Npgsql
/// converter, which this assembly cannot reach. Mutating the process timezone would add no signal
/// while racing the ~1750 tests this assembly runs in parallel.
/// </para>
/// </summary>
public sealed class QueueDistributionWorkerCycleFailureLoggingTests
{
    /// <summary>Exact text of the <c>[LoggerMessage]</c> the inner catch emits.</summary>
    private const string DistributionCycleFailed = "Distribution cycle failed";

    private const string TestTenantId = "t-tz-001";

    /// <summary>Hard wall-clock cap on the causal waits so a wedged worker cannot hang the suite.</summary>
    private static readonly TimeSpan WaitCap = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ExecuteAsync_ShouldNotLogDistributionCycleFailed_WhenCyclesCompleteSuccessfully()
    {
        var queueId = EntityId.From("q1");
        var agentId = EntityId.From("a1");
        // CreatedAt carries a non-zero offset — the shape a UTC-5 environment produces at ingress.
        // This pins that the worker path is offset-agnostic; it does NOT exercise the Npgsql
        // projection (see the class-level scope note).
        var conversation = MakeQueuedConversation(queueId, new DateTimeOffset(2026, 8, 20, 7, 0, 0, TimeSpan.FromHours(-5)));

        var conversationStore = Substitute.For<IConversationStore>();
        var tenantStore = Substitute.For<ITenantStore>();
        var agentSelector = Substitute.For<IAgentSelector>();
        var switchboard = Substitute.For<IConversationSwitchboard>();

        var cycles = 0;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref cycles) >= 3)
                    settled.TrySetResult();
                return (IReadOnlyList<Tenant>)[MakeActiveTenant()];
            });
        conversationStore.ListQueuedAsync(Arg.Any<TenantId>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Conversation>)[conversation]);
        conversationStore.GetByIdAsync(Arg.Any<TenantId>(), conversation.ConversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        agentSelector.SelectAgentAsync(
                Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(),
                Arg.Any<EntityId?>(), Arg.Any<CancellationToken>())
            .Returns(agentId);
        switchboard.OfferToAgentAsync(
                Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(new OwnershipResult(true, ConversationOwner.ForAgent(agentId), ConversationState.Offered, null));

        var logger = new CapturingLogger<QueueDistributionWorker>();
        using var eventBus = new PlatformEventBus();
        using var sut = BuildWorker(conversationStore, tenantStore, agentSelector, switchboard, eventBus, logger);

        await sut.StartAsync(CancellationToken.None);
        await settled.Task.WaitAsync(WaitCap);
        await sut.StopAsync(CancellationToken.None);

        // The cycles did real work rather than short-circuiting on an empty tenant/queue list —
        // otherwise "no failure logged" would be a statement about a no-op.
        await switchboard.Received().OfferToAgentAsync(
            conversation.ConversationId, Arg.Any<TenantId>(), agentId, Arg.Any<CancellationToken>());

        logger.CountOf(DistributionCycleFailed).Should().Be(0);
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopLoggingDistributionCycleFailed_WhenTheFailingCycleRecovers()
    {
        // The reported production shape: every cycle throws, so the worker logs once per tick
        // forever. This pins BOTH halves of the scenario's second clause — the loop survives each
        // failure (it is not wedged), and the logging stops as soon as the cause clears.
        const int failingCycles = 2;

        var conversationStore = Substitute.For<IConversationStore>();
        var tenantStore = Substitute.For<ITenantStore>();

        var cycles = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var n = Interlocked.Increment(ref cycles);
                if (n <= failingCycles)
                    throw new InvalidOperationException("simulated store failure");

                if (n == failingCycles + 1)
                    recovered.TrySetResult();
                if (n >= failingCycles + 3)
                    settled.TrySetResult();

                return (IReadOnlyList<Tenant>)[];
            });

        var logger = new CapturingLogger<QueueDistributionWorker>();
        using var eventBus = new PlatformEventBus();
        using var sut = BuildWorker(
            conversationStore, tenantStore, Substitute.For<IAgentSelector>(),
            Substitute.For<IConversationSwitchboard>(), eventBus, logger);

        await sut.StartAsync(CancellationToken.None);

        // By the time the first post-recovery cycle enters the store, every failing cycle has
        // already run its inner catch, so this snapshot is complete and race-free.
        await recovered.Task.WaitAsync(WaitCap);
        var loggedWhileFailing = logger.CountOf(DistributionCycleFailed);

        await settled.Task.WaitAsync(WaitCap);
        var loggedAfterRecovery = logger.CountOf(DistributionCycleFailed);

        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
        await sut.StopAsync(CancellationToken.None);

        // Positive control for the sibling test: the capture really does match the emitted text.
        loggedWhileFailing.Should().Be(failingCycles);
        loggedAfterRecovery.Should().Be(failingCycles);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static QueueDistributionWorker BuildWorker(
        IConversationStore conversationStore,
        ITenantStore tenantStore,
        IAgentSelector agentSelector,
        IConversationSwitchboard switchboard,
        PlatformEventBus eventBus,
        ILogger<QueueDistributionWorker> logger)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(_ => DateTimeOffset.UtcNow);

        return new QueueDistributionWorker(
            conversationStore,
            tenantStore,
            agentSelector,
            switchboard,
            eventBus,
            clock,
            new ServiceHeartbeat(),
            // Fast cadence + no warm-up so the REAL ExecuteAsync loop (and therefore the real inner
            // try-catch that emits the log under test) ticks in milliseconds. C3 — the loop is driven
            // causally off the store call, never off a wall-clock sleep.
            Options.Create(new DistributionOptions { PollIntervalMs = 25, QueueDistributionStartupDelayMs = 0 }),
            logger);
    }

    private static Tenant MakeActiveTenant() => new()
    {
        TenantId = TestTenantId,
        Name = "Timezone Regression Tenant",
        Status = TenantStatus.Active,
    };

    private static Conversation MakeQueuedConversation(EntityId queueId, DateTimeOffset createdAt) => new()
    {
        ConversationId = EntityId.New(),
        TenantId = new TenantId(TestTenantId),
        ContactId = EntityId.New(),
        Channel = ChannelType.WhatsApp,
        Owner = ConversationOwner.ForQueue(queueId),
        State = ConversationState.Queued,
        CreatedAt = createdAt,
    };

    /// <summary>
    /// Minimal capturing logger — records the formatted text of every log call so a test can count
    /// occurrences of a specific <c>[LoggerMessage]</c> without an external test-logging package.
    /// Formatted text (not <c>EventId</c>) is the assertion key because the generated ids are not
    /// declared in source, while the message literal is.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _messages.Enqueue(formatter(state, exception));
        }

        public int CountOf(string message)
            => _messages.Count(m => string.Equals(m, message, StringComparison.Ordinal));
    }
}
