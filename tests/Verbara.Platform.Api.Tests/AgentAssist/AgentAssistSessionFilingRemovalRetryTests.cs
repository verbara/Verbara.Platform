using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// Platform #650: a failed removal of a session's Monitor record, at the session's end or at a tenant conflict, is retried
/// a bounded number of times with a short backoff, so an ended or conflicted session stops being found well before its
/// record expires (last filing + 15 s). The clock is a <see cref="FakeTimeProvider"/>: no wall-clock sleep.
/// </summary>
public sealed class AgentAssistSessionFilingRemovalRetryTests : IDisposable
{
    private const string Demo = "demo";
    private const string Acme = "acme";
    private const string SessionId = "as-removal-1";

    /// <summary>The step each test advances the clock by: the shortest retry delay.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
    private readonly FailingRemovalStore _store;
    private readonly CapturingLogger _logger = new();
    private readonly List<AgentAssistSessionFiling> _filings = [];

    public AgentAssistSessionFilingRemovalRetryTests() => _store = new FailingRemovalStore(new MemoryLiveSessionStore(_time));

    public void Dispose()
    {
        foreach (var filing in _filings)
            filing.Dispose();
    }

    private static CallSession Call(string? tenant) =>
        new("call-removal-1", "1712345678.60", "server-1", CallDirection.Inbound) { TenantId = tenant, AgentId = "agent-7", QueueName = "support" };

    private async Task<AgentAssistSessionFiling> StartAsync(string startTimeTenant, CallSession call)
    {
        var filing = AgentAssistSessionFiling.Start(SessionId, startTimeTenant, call, _store, _time, _logger);
        _filings.Add(filing);
        await filing.WhenIdleAsync();
        return filing;
    }

    /// <summary>Advances the clock <paramref name="steps"/> times by <see cref="Step"/>, letting the queue drain after each.</summary>
    private async Task StepAsync(AgentAssistSessionFiling filing, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            _time.Advance(Step);
            await filing.WhenIdleAsync();
        }
    }

    private Task<LiveSessionRecord?> FindAsync(string tenant) => _store.FindAsync(tenant, SessionId, CancellationToken.None);

    [Fact]
    public async Task EndAsync_ShouldRetryTheRemovalUntilItLands_WhenTheFirstRemovalsFail()
    {
        var filing = await StartAsync(Demo, Call(Demo));
        _store.FailNextRemovals(2);

        await filing.EndAsync();
        (await FindAsync(Demo)).Should().NotBeNull("precondition: the first removal failed");

        await StepAsync(filing, 8);   // 2 s, far inside the record's 15 s expiry

        _store.RemovalAttempts.Should().Be(3, "two failures, then the retry that lands");
        (await FindAsync(Demo)).Should().BeNull("a retried removal ends the session's record within about 2 s");
    }

    [Fact]
    public async Task EndAsync_ShouldStopRetryingAndLogIt_WhenEveryRemovalFails()
    {
        var filing = await StartAsync(Demo, Call(Demo));
        _store.FailNextRemovals(int.MaxValue);

        await filing.EndAsync();
        await StepAsync(filing, 4 * 60);   // one minute

        _store.RemovalAttempts.Should().BeInRange(2, AgentAssistSessionFiling.MaxRemovalAttempts,
            "a failed removal is retried, a bounded number of times");
        _store.RemovalAttempts.Should().Be(AgentAssistSessionFiling.MaxRemovalAttempts);
        _logger.Warnings.Should().HaveCount(AgentAssistSessionFiling.MaxRemovalAttempts, "each failed attempt is logged");
        _logger.Warnings[^1].Should().Contain("gave up", "the last one says the record is left to its expiry");
        (await FindAsync(Demo)).Should().BeNull("the record expired, last filing + 15 s, so the session is not found");
    }

    [Fact]
    public async Task OnEmission_ShouldRetryAFailedConflictRemovalBeforeTheNextHeartbeat_WhenTheCallIsRestampedToAConflictingTenant()
    {
        var call = Call(Demo);
        var filing = await StartAsync(Demo, call);
        _store.FailNextRemovals(1);

        call.TenantId = Acme;
        filing.OnEmission();
        await filing.WhenIdleAsync();
        (await FindAsync(Demo)).Should().NotBeNull("precondition: the conflict's removal failed");

        await StepAsync(filing, 4);   // 1 s, inside one 5 s heartbeat

        (await FindAsync(Demo)).Should().BeNull("the failed conflict removal is retried before the next heartbeat");
        (await FindAsync(Acme)).Should().BeNull("a conflicted session is listed to nobody");
        _store.RemovalAttempts.Should().Be(2);
    }

    [Fact]
    public async Task EndAsync_ShouldKeepTheRecordALaterFilingWrote_WhenARetriedRemovalLandsAfterTheSessionIsFiledAgain()
    {
        // A transferred or reconnected call starts a new generation of the same session id, filed under the same tenant.
        var ended = await StartAsync(Demo, Call(Demo));
        _store.FailNextRemovals(1);
        await ended.EndAsync();

        _time.Advance(TimeSpan.FromMilliseconds(100));
        var next = await StartAsync(Demo, Call(Demo));
        (await FindAsync(Demo)).Should().NotBeNull("precondition: the new generation filed its record");

        await StepAsync(ended, 8);   // the ended filing's retry fires and lands
        await next.WhenIdleAsync();

        _store.RemovalAttempts.Should().Be(2, "precondition: the ended filing retried its removal");
        (await FindAsync(Demo)).Should().NotBeNull(
            "a retry removes only the copy its own filing wrote, never one a later filing of the same session wrote");
    }

    [Fact]
    public async Task EndAsync_ShouldRetryTheRemoval_WhenAnEarlierConflictRemovalGaveUpAndTheOwnerCameBack()
    {
        var call = Call(Demo);
        var filing = await StartAsync(Demo, call);
        _store.FailNextRemovals(AgentAssistSessionFiling.MaxRemovalAttempts);
        call.TenantId = Acme;
        filing.OnEmission();
        await filing.WhenIdleAsync();
        await StepAsync(filing, 8);   // every retry of the conflict's removal fails: it gives up
        _store.RemovalAttempts.Should().Be(AgentAssistSessionFiling.MaxRemovalAttempts, "precondition: it gave up");

        call.TenantId = Demo;         // the conflict ends; the record is still filed under demo
        filing.OnEmission();
        await StepAsync(filing, 13);  // past the next heartbeat, which refreshes it
        _store.FailNextRemovals(1);

        await filing.EndAsync();
        await StepAsync(filing, 8);

        (await FindAsync(Demo)).Should().BeNull("the end's removal has its own retries, whatever an earlier removal used");
    }

    /// <summary>A <see cref="MemoryLiveSessionStore"/> whose next removals fail as a Redis outage does.</summary>
    private sealed class FailingRemovalStore(MemoryLiveSessionStore inner) : IAgentAssistLiveSessionStore
    {
        private int _failures;

        public int RemovalAttempts { get; private set; }

        public void FailNextRemovals(int count) => _failures = count;

        public bool IsShared => true;

        public Task FileAsync(LiveSessionRecord record, DateTimeOffset expiresAt, CancellationToken ct) =>
            inner.FileAsync(record, expiresAt, ct);

        public Task RemoveAsync(string ownerTenant, string sessionId, DateTimeOffset? filedUntil, CancellationToken ct)
        {
            RemovalAttempts++;
            if (_failures > 0)
            {
                _failures--;
                return Task.FromException(new RedisException("removal failed"));
            }

            return inner.RemoveAsync(ownerTenant, sessionId, filedUntil, ct);
        }

        public Task<IReadOnlyList<LiveSessionRecord>> ListAsync(string tenant, CancellationToken ct) => inner.ListAsync(tenant, ct);

        public Task<LiveSessionRecord?> FindAsync(string callerTenant, string sessionId, CancellationToken ct) =>
            inner.FindAsync(callerTenant, sessionId, ct);
    }

    /// <summary>Keeps the message of every warning written.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_gate)
                    return [.. _warnings];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                return;
            lock (_gate)
                _warnings.Add(formatter(state, exception));
        }
    }
}
