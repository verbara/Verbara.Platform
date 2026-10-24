using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Pro.Cluster;
using Verbara.Sdk.Pro.Dialer;
using Verbara.Sdk.Pro.Dialer.Results;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-places-calls-end-to-end (design D3 point 7, D9; task 2.2): a host wiring pin, not a regression test. With a
/// dialer connection string the Api runs Pro's <see cref="CallOutcomeObserver"/>, the one thing that records each
/// placed call and releases its slot, and the dialer's node is a key of the cluster's initial nodes, so every
/// activation originates on a node that exists. It boots the real Api <c>Program</c> against a Testcontainers
/// Postgres, because <c>UsePostgresDialerStorage</c> runs the dialer migrations (V003 included) at registration
/// (precedent <c>ComposedDataSourceNamesTests</c>).
/// </summary>
public sealed class DialerHostWiringTests : IAsyncLifetime
{
    private IContainer? _postgres;

    [Fact]
    public void Boot_ShouldRunTheObserverOnTheClustersNode_WhenADialerConnectionStringIsSet()
    {
        using var factory = new DialerHostWiringFactory(ConnectionString("dialer"), ConnectionString("cluster"));

        var services = factory.Services; // boots Program.cs: the dialer migrations, AddProDialer, the cluster

        factory.CapturedObservers.Should().NotBeEmpty(
            "AddProDialer registers the call-outcome observer as a hosted service of its own type");
        services.GetServices<IHostedService>().OfType<CallOutcomeObserver>().Should().ContainSingle(
            "exactly one observer records the calls this process places and releases their slots");

        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.IsCancellationRequested.Should().BeTrue(
            "the host started with the observer, so its start resolved no external seam under the stubbed pool and AMI");
        lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse(
            "a faulted observer would stop the host under BackgroundServiceExceptionBehavior.StopHost");

        var dialer = services.GetRequiredService<IOptions<DialerOptions>>().Value;
        var cluster = services.GetRequiredService<IOptions<ClusterOptions>>().Value;
        cluster.InitialNodes.Should().ContainKey(dialer.NodeId,
            "every activation originates on the dialer's node, which must be the node the cluster registers the Api's Asterisk server under");
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP: the image's entrypoint first runs a socket-only temporary
            // server, so a socket probe greens before 5432 accepts connections.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    /// <summary>The container's connection string for one role, named in <c>Application Name</c>.</summary>
    private string ConnectionString(string role) =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        $"Database=postgres;Username=postgres;Password=postgres;Application Name=dialer-wiring-{role}";
}
