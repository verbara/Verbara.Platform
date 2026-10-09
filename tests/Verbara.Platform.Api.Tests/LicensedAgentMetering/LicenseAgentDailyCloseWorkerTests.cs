using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// licensed-agent-metering slice 2 (tasks.md 5.6; design D7): the worker resolves its storage on first use,
/// checks the day zone at its first tick (never at host start, never without Postgres), stops loudly on a
/// changed or unknown zone, waits for the licence load attempt before anchoring, closes every tick, purges once
/// a day, and never consults licence validity.
/// </summary>
public sealed class LicenseAgentDailyCloseWorkerTests
{
    private readonly ServiceHeartbeat _heartbeat = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly ILicenseAgentDailyClose _close = Substitute.For<ILicenseAgentDailyClose>();
    private readonly ILicenseAgentRetentionPurge _purge = Substitute.For<ILicenseAgentRetentionPurge>();

    public LicenseAgentDailyCloseWorkerTests()
    {
        _close.CloseAsync(default!, default, default).ReturnsForAnyArgs(new LicenseAgentCloseResult(0, 0, 0, 0));
    }

    [Fact]
    public async Task TickAsync_ShouldReturnWithoutAnyDayZoneCheck_WhenNoPostgresStorageIsConfigured()
    {
        var worker = Worker(new ServiceCollection().BuildServiceProvider(), dayZone: "America/Bogota");

        (await worker.TickAsync(CancellationToken.None)).Should().BeFalse();

        worker.Stopped.Should().BeFalse("no storage is not an error: the export and design-time hosts boot without one");
        _heartbeat.GetAll().Should().BeEmpty("a worker that never runs reports nothing");
    }

    [Fact]
    public async Task TickAsync_ShouldStopAndReportUnhealthyAtOnce_WhenTheDayZoneChanged()
    {
        _close.PrepareAsync("America/Bogota", Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new LicenseAgentDayZoneMismatchException("UTC", "America/Bogota"));
        var worker = Worker(Provider(), dayZone: "America/Bogota");

        (await worker.TickAsync(CancellationToken.None)).Should().BeFalse();

        worker.Stopped.Should().BeTrue();
        _heartbeat.IsHealthy(nameof(LicenseAgentDailyCloseWorker)).Should().BeFalse();
        await _close.DidNotReceiveWithAnyArgs().CloseAsync(default!, default, default);
        await _purge.DidNotReceiveWithAnyArgs().PurgeAsync(default!, default, default);
    }

    [Fact]
    public async Task TickAsync_ShouldStop_WhenTheDayZoneIsUnknown()
    {
        var worker = Worker(Provider(), dayZone: "Mars/Olympus_Mons");

        (await worker.TickAsync(CancellationToken.None)).Should().BeFalse();

        worker.Stopped.Should().BeTrue();
        await _close.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default);
    }

    [Fact]
    public async Task TickAsync_ShouldPrepareOnceWithUtcByDefault_CloseEveryTick_AndPurgeOncePerDay()
    {
        var worker = Worker(Provider(), dayZone: null);

        for (var i = 0; i < 3; i++)
        {
            (await worker.TickAsync(CancellationToken.None)).Should().BeTrue();
            _time.Advance(TimeSpan.FromMinutes(15));
        }

        _time.Advance(TimeSpan.FromDays(1));
        await worker.TickAsync(CancellationToken.None);

        await _close.Received(1).PrepareAsync("UTC", Arg.Any<CancellationToken>());
        await _close.Received(4).CloseAsync(TimeZoneInfo.Utc, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _purge.Received(2).PurgeAsync(TimeZoneInfo.Utc, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        _heartbeat.IsHealthy(nameof(LicenseAgentDailyCloseWorker)).Should().BeTrue();
    }

    [Fact]
    public async Task TickAsync_ShouldCloseWithoutReadingLicenceState_WhenTheLicenceIsExpired()
    {
        var licence = Substitute.For<ILicenseStatus>();
        licence.IsValid.Returns(false);
        licence.ExpiresAt.Returns(DateTimeOffset.UtcNow.AddYears(-1));
        var services = new ServiceCollection();
        services.AddSingleton(_close);
        services.AddSingleton(_purge);
        services.AddSingleton(licence);
        var worker = Worker(services.BuildServiceProvider(), dayZone: "UTC");

        await worker.TickAsync(CancellationToken.None);

        await _close.ReceivedWithAnyArgs(1).CloseAsync(default!, default, default);
        licence.ReceivedCalls().Should().BeEmpty("the close never depends on licence state");
    }

    [Fact]
    public async Task TickAsync_ShouldRetryTheFirstTickStep_WhenTheDatabaseIsUnreachable()
    {
        _close.PrepareAsync("UTC", Arg.Any<CancellationToken>()).Returns(
            _ => throw new InvalidOperationException("database unreachable"), _ => Task.FromResult(1));
        var worker = Worker(Provider(), dayZone: "UTC");

        (await worker.TickAsync(CancellationToken.None)).Should().BeTrue();
        (await worker.TickAsync(CancellationToken.None)).Should().BeTrue();

        await _close.Received(2).PrepareAsync("UTC", Arg.Any<CancellationToken>());
        await _close.ReceivedWithAnyArgs(1).CloseAsync(default!, default, default);
        worker.Stopped.Should().BeFalse();
    }

    [Fact]
    public async Task TickAsync_ShouldNotAnchorBeforeTheLicenceLoadCompletes_AndAnchorWithTheLoadedLicenceIdAfter()
    {
        // The lab acceptance run (verbara-lab results/2026-10-09-57ab4f9a): the worker's first tick ran before the
        // licence validation service had loaded the .lic, anchored the deployment chain with a null licence id, and
        // a chain_reanchored row followed on the next tick.
        var tracker = new LicenseStatusTracker(_time);
        var source = new ProLicenseIdSource(tracker);
        var anchoredWith = new List<string?>();
        _close.PrepareAsync("UTC", Arg.Any<CancellationToken>())
            .Returns(_ => { anchoredWith.Add(source.CurrentLicenseId); return Task.FromResult(1); });
        var worker = Worker(Provider(source), dayZone: "UTC");

        (await worker.TickAsync(CancellationToken.None)).Should().BeTrue("waiting for the licence load is not a stop");

        await _close.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default);
        await _close.DidNotReceiveWithAnyArgs().CloseAsync(default!, default, default);
        await _purge.DidNotReceiveWithAnyArgs().PurgeAsync(default!, default, default);
        worker.Stopped.Should().BeFalse();
        _heartbeat.GetAll().Should().ContainKey(nameof(LicenseAgentDailyCloseWorker))
            .WhoseValue.IsHealthy.Should().BeTrue("a licence still loading is transient, not a fault");

        tracker.Update(LicenseValidationResult.Valid, Key("lic-0001"));
        (await worker.TickAsync(CancellationToken.None)).Should().BeTrue();

        anchoredWith.Should().Equal(["lic-0001"]);
        await _close.ReceivedWithAnyArgs(1).CloseAsync(default!, default, default);
    }

    [Fact]
    public async Task TickAsync_ShouldAnchorWithTheNullForm_WhenTheLoadAttemptFoundNoLicence()
    {
        var tracker = new LicenseStatusTracker(_time);
        var source = new ProLicenseIdSource(tracker);
        var anchoredWith = new List<string?>();
        _close.PrepareAsync("UTC", Arg.Any<CancellationToken>())
            .Returns(_ => { anchoredWith.Add(source.CurrentLicenseId); return Task.FromResult(1); });
        tracker.Update(LicenseValidationResult.Invalid, null);
        var worker = Worker(Provider(source), dayZone: "UTC");

        (await worker.TickAsync(CancellationToken.None)).Should().BeTrue();

        anchoredWith.Should().Equal([(string?)null], "metering never waits for a valid licence, only for the load attempt (ADR-0020 §6)");
        await _close.ReceivedWithAnyArgs(1).CloseAsync(default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetryWithinSeconds_WhileTheLicenceIsLoading()
    {
        var time = new TimerSignallingTimeProvider(_time.GetUtcNow());
        var tracker = new LicenseStatusTracker(time);
        // The licence loads right after the worker's first tick has seen it still loading.
        var source = new LoadsAfterFirstWaitSource(tracker, () => tracker.Update(LicenseValidationResult.Valid, Key("lic-0001")));
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _close.PrepareAsync("UTC", Arg.Any<CancellationToken>())
            .Returns(_ => { prepared.TrySetResult(); return Task.FromResult(1); });
        var worker = new LicenseAgentDailyCloseWorker(
            Provider(source), Configuration("UTC"), _heartbeat, NullLogger<LicenseAgentDailyCloseWorker>.Instance, time);
        using var cts = new CancellationTokenSource();

        await worker.StartAsync(cts.Token);
        await source.FirstWait.WaitAsync(TimeSpan.FromSeconds(30));
        // The waiting tick has scheduled its next tick: one retry interval later it must anchor.
        await time.FirstTimer.WaitAsync(TimeSpan.FromSeconds(30));
        prepared.Task.IsCompleted.Should().BeFalse("nothing is anchored while the licence is loading");
        time.Advance(LicenseAgentDailyCloseWorker.LicenceLoadRetryInterval);

        try
        {
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // Asserted below.
        }

        prepared.Task.IsCompleted.Should().BeTrue(
            "the first anchor must not wait a full 15-minute tick for a licence that loads a moment after start");
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }

    /// <summary>A fake clock that signals when the first timer (the worker's first delay) is scheduled.</summary>
    private sealed class TimerSignallingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly TaskCompletionSource _firstTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstTimer => _firstTimer.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _firstTimer.TrySetResult();
            return timer;
        }
    }

    /// <summary>Reports the tracker's load state; the first time it reports "still loading" it loads the licence.</summary>
    private sealed class LoadsAfterFirstWaitSource(LicenseStatusTracker tracker, Action load) : ILicenseIdSource
    {
        private readonly ProLicenseIdSource _inner = new(tracker);
        private readonly TaskCompletionSource _firstWait = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstWait => _firstWait.Task;

        public string? CurrentLicenseId => _inner.CurrentLicenseId;

        public bool LoadCompleted
        {
            get
            {
                var completed = _inner.LoadCompleted;
                if (!completed && _firstWait.TrySetResult())
                    load();
                return completed;
            }
        }
    }

    private static LicenseKey Key(string id) => new(
        id, "Licensee", LicenseTier.SelfHostStartup, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LicenseFeature.None, 25, 1, "sig");

    private ServiceProvider Provider(ILicenseIdSource source)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_close);
        services.AddSingleton(_purge);
        services.AddSingleton(source);
        return services.BuildServiceProvider();
    }

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_close);
        services.AddSingleton(_purge);
        return services.BuildServiceProvider();
    }

    private LicenseAgentDailyCloseWorker Worker(IServiceProvider services, string? dayZone) => new(
        services, Configuration(dayZone), _heartbeat, NullLogger<LicenseAgentDailyCloseWorker>.Instance, _time);

    private static IConfiguration Configuration(string? dayZone) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [LicenseAgentDayZone.ConfigurationKey] = dayZone })
        .Build();
}
