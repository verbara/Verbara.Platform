using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Verbara.Platform.Api.Tests.Logging;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The startup RBAC seed (<see cref="RbacSeedStartup"/>): skipped without Postgres, logged when it
/// completes, logged at Error and never thrown when it fails (Platform/ADR-0037), and handed the host's
/// logger factory so the role migration can log the tenants it fails or skips.
/// </summary>
public sealed class RbacSeedStartupTests
{
    [Fact]
    public async Task RunAsync_ShouldSeedNothingAndLogNothing_WhenNoNpgsqlDataSourceIsRegistered()
    {
        using var capture = new LogRecordCapture();
        await using var services = new ServiceCollection().BuildServiceProvider();

        await RbacSeedStartup.RunAsync(services, capture.CreateLogger("startup"), CancellationToken.None);

        capture.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ShouldPassTheHostLoggerFactoryAndLogCompletion_WhenTheSeedSucceeds()
    {
        using var capture = new LogRecordCapture();
        await using var services = ServicesWithDataSource(out var loggerFactory);
        ILoggerFactory? handedOver = null;

        await RbacSeedStartup.RunAsync(
            services, capture.CreateLogger("startup"),
            (_, factory, _) => { handedOver = factory; return Task.CompletedTask; },
            CancellationToken.None);

        handedOver.Should().BeSameAs(loggerFactory);
        capture.Records.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task RunAsync_ShouldLogTheFailureAtErrorAndNotThrow_WhenTheSeedThrows()
    {
        using var capture = new LogRecordCapture();
        await using var services = ServicesWithDataSource(out _);
        var failure = new InvalidOperationException("RBAC role migration failed for 1 of 3 tenants (b).");

        var run = () => RbacSeedStartup.RunAsync(
            services, capture.CreateLogger("startup"), (_, _, _) => throw failure, CancellationToken.None);

        await run.Should().NotThrowAsync("a failed seed must not stop the host from booting");
        var record = capture.Records.Should().ContainSingle().Subject;
        record.Level.Should().Be(LogLevel.Error);
        record.Exception.Should().Contain("1 of 3 tenants");
    }

    [Fact]
    public async Task RunAsync_ShouldLetCancellationPropagateWithoutLoggingAFailure_WhenTheBootIsCancelled()
    {
        using var capture = new LogRecordCapture();
        await using var services = ServicesWithDataSource(out _);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var run = () => RbacSeedStartup.RunAsync(
            services, capture.CreateLogger("startup"),
            (_, _, ct) => Task.FromCanceled(ct), cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        capture.Records.Should().BeEmpty();
    }

    private static ServiceProvider ServicesWithDataSource(out ILoggerFactory loggerFactory)
    {
        loggerFactory = LoggerFactory.Create(_ => { });
        var collection = new ServiceCollection();
        // Never opened: the seed under test is a delegate, so no connection is attempted. Registered through a
        // factory so the provider owns it and disposes it.
        collection.AddSingleton(_ => NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none"));
        collection.AddSingleton(loggerFactory);
        return collection.BuildServiceProvider();
    }
}
