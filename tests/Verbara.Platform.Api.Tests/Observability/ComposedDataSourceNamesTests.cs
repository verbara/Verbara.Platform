using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;

namespace Verbara.Platform.Api.Tests.Observability;

/// <summary>
/// measure-api-multi-replica-baseline (D2), at the composition root: every pool <c>Program.cs</c> opens is
/// labelled on <c>/metrics</c> with its fixed name, and none falls back to its connection string. Boots
/// the real Api <c>Program</c> against a Testcontainers Postgres, because the host applies the Platform
/// migrations and the cluster-lock schema before it serves. Each role gets its own connection string,
/// so each builds its own pool rather than sharing the core one.
/// </summary>
public sealed class ComposedDataSourceNamesTests : IAsyncLifetime
{
    private IContainer? _postgres;

    [Fact]
    public void Boot_ShouldLabelEveryPoolWithItsFixedName_WhenEachRoleHasItsOwnConnectionString()
    {
        var marker = Guid.NewGuid().ToString("N");
        using var factory = new PlatformApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", ConnectionString(marker, "core"));
            builder.UseSetting("ConnectionStrings:Dialer", ConnectionString(marker, "dialer"));
            builder.UseSetting("ConnectionStrings:Cluster", ConnectionString(marker, "cluster"));
            builder.UseSetting("ConnectionStrings:Realtime", ConnectionString(marker, "realtime"));
            builder.UseSetting("ConnectionStrings:Analytics", ConnectionString(marker, "analytics"));
            builder.UseSetting("ConnectionStrings:AnalyticsLive", ConnectionString(marker, "analytics-live"));
        });

        _ = factory.Services; // boots Program.cs: migrations, one data source per role, the cluster-lock schema

        var poolTags = NpgsqlPoolTags.ReportedNow();
        poolTags.Should().Contain(
            ["core", "dialer", "cluster", "realtime", "analytics", "analytics-live"],
            "each pool the composition root builds carries the fixed name of its role");
        poolTags.Should().NotContain(tag => tag.Contains(marker, StringComparison.Ordinal),
            "a pool labelled with its connection string would put the host, database and user on an unauthenticated endpoint");
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

    /// <summary>
    /// The container's connection string for one role. The role and the test's marker go in
    /// <c>Application Name</c>, so every role's string is distinct and a pool that falls back to its
    /// connection string as its name is recognisable in a pool tag.
    /// </summary>
    private string ConnectionString(string marker, string role) =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        $"Database=postgres;Username=postgres;Password=postgres;Application Name={marker}-{role}";
}
