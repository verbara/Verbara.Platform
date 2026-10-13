using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Verbara.Platform.Storage.Postgres.Tests;

/// <summary>
/// Testcontainers-backed Postgres for <c>NamedDataSourceTests</c>: an empty <c>postgres:16-alpine</c>
/// database, because the tests read only the Npgsql pool and operation instruments, never a table.
/// </summary>
public sealed class NamedDataSourceFixture : IAsyncLifetime
{
    private IContainer? _container;

    /// <summary>
    /// The container's connection string, carrying <paramref name="applicationName"/> as its
    /// <c>Application Name</c>, so a pool that falls back to the connection string as its diagnostics
    /// name can be recognised in a pool tag.
    /// </summary>
    public string ConnectionString(string applicationName) =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        $"Database=postgres;Username=postgres;Password=postgres;Application Name={applicationName}";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    // `-h 127.0.0.1` probes over TCP: the entrypoint's temporary server listens on the
                    // Unix socket only, so a socket probe greens before 5432 accepts connections (see
                    // ApiKeyStoreLastUsedFixture).
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
