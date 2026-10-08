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
/// changed or unknown zone, closes every tick, purges once a day, and never consults licence state.
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

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_close);
        services.AddSingleton(_purge);
        return services.BuildServiceProvider();
    }

    private LicenseAgentDailyCloseWorker Worker(IServiceProvider services, string? dayZone)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [LicenseAgentDayZone.ConfigurationKey] = dayZone })
            .Build();
        return new LicenseAgentDailyCloseWorker(
            services, configuration, _heartbeat, NullLogger<LicenseAgentDailyCloseWorker>.Instance, _time);
    }
}
