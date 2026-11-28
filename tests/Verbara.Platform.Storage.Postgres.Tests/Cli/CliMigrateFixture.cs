using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using NpgsqlTypes;

namespace Verbara.Platform.Storage.Postgres.Tests.Cli;

/// <summary>
/// cli-migrate-prints-migration-notices (#711): one Testcontainers Postgres, a fresh database per test holding the
/// REAL embedded migrations up to (not including) a given one — as an older release left it — and the built
/// <c>Verbara.Platform.Cli</c> run as its own process, the way an operator runs it.
/// </summary>
public sealed class CliMigrateFixture : IAsyncLifetime
{
    private const string ResourcePrefix = "Verbara.Platform.Storage.Postgres.Migrations.";

    private IContainer? _container;

    public string ConnectionStringFor(string database) =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        $"Database={database};Username=postgres;Password=postgres";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    // `-h 127.0.0.1` forces the readiness probe over TCP (see MigrationsFixture).
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>A new, empty database; returns its connection string.</summary>
    public async Task<string> EmptyDatabaseAsync()
    {
        var name = $"cli_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(ConnectionStringFor("postgres")))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();
        return ConnectionStringFor(name);
    }

    /// <summary>
    /// A new database holding every migration before <paramref name="migration"/>, each recorded in a
    /// <c>_migrations</c> table created with <paramref name="trackingTableDdl"/>; returns its connection string.
    /// </summary>
    public async Task<string> DatabaseBeforeAsync(string migration, string trackingTableDdl)
    {
        var connectionString = await EmptyDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await ExecuteAsync(ds, trackingTableDdl);
        foreach (var (file, sql) in Migrations().Where(m => string.CompareOrdinal(m.File, migration) < 0))
        {
            await ExecuteAsync(ds, sql);
            await ExecuteAsync(ds, "INSERT INTO _migrations (name, applied_at) VALUES (@Name, now())",
                new NpgsqlParameter("Name", NpgsqlDbType.Text) { Value = file });
        }

        return connectionString;
    }

    /// <summary>Runs <c>Verbara.Platform.Cli</c> with <paramref name="arguments"/> and returns its exit code and output.</summary>
    public static async Task<CliRun> RunCliAsync(params string[] arguments)
    {
        var cli = Path.Combine(AppContext.BaseDirectory, "Verbara.Platform.Cli.dll");
        File.Exists(cli).Should().BeTrue("the test project references the CLI, so its build output carries it");

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return new CliRun(process.ExitCode, await stdout, await stderr);
    }

    public static async Task ExecuteAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await ExecuteAsync(ds, sql, parameters);
    }

    public static async Task<long> CountAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource ds, string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private static IEnumerable<(string File, string Sql)> Migrations()
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[ResourcePrefix.Length..], reader.ReadToEnd());
        }
    }
}

/// <summary>One run of the CLI.</summary>
public sealed record CliRun(int ExitCode, string StandardOutput, string StandardError)
{
    public override string ToString() => $"exit {ExitCode}; stdout:\n{StandardOutput}\nstderr:\n{StandardError}";
}
