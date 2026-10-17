using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.Dialer.Storage.Postgres;
using Verbara.Sdk.Pro.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// #385 — DI wiring: the registration-reloading decorator wraps the realtime-syncing trunk store on the
/// dialer branch only, and a voice-AMI pod gets the reload worker without touching AMI at resolution.
/// </summary>
public sealed class TrunkRegistrationReloadRegistrationTests
{
    [Fact]
    public void AddRealtimeSyncingTrunkStore_ShouldWrapTheSyncingStore_WhenDialerIsConfigured()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DialerDbContext("Host=127.0.0.1;Database=unused"));
        services.AddSingleton(Substitute.For<IRealtimeSyncService>());

        services.AddRealtimeSyncingTrunkStore("Host=127.0.0.1;Database=unused");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TrunkStoreBase>().Should().BeOfType<RegistrationReloadingTrunkStore>();
        provider.GetRequiredService<TrunkRegistrationReloadSignal>().IsDirty.Should().BeFalse();
    }

    [Fact]
    public void AddRealtimeSyncingTrunkStore_ShouldKeepTheNoOpStore_WhenDialerIsNotConfigured()
    {
        var services = new ServiceCollection();

        services.AddRealtimeSyncingTrunkStore(null);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TrunkStoreBase>().Should().NotBeOfType<RegistrationReloadingTrunkStore>();
    }

    [Fact]
    public async Task AddTrunkAmiServices_ShouldRegisterTheWorkerAndTester_WithoutAnAmiServer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance));
        services.AddKeyedSingleton(VoiceLeaderResources.AmiOwner, Substitute.For<IClusterLeader>());
        services.AddSingleton(Substitute.For<TrunkStoreBase>());

        services.AddTrunkAmiServices();
        await using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().Should().ContainSingle(s => s is TrunkRegistrationReloadWorker);
        provider.GetRequiredService<ITrunkConnectivityTester>().Should().BeOfType<TrunkConnectivityTester>();
    }
}
