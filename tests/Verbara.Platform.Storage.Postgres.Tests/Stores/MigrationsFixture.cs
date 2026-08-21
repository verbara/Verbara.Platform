using System.Reflection;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture that applies the REAL embedded
/// <c>Migrations/*.sql</c> resources from the <c>Verbara.Platform.Storage.Postgres</c>
/// assembly to a clean database — exercising the actual migration files
/// (incl. <c>002_reason_hints.sql</c>) and their name-ordinal ordering, mirroring
/// the production <c>DatabaseMigrationService</c> runner. Used by
/// <see cref="MigrationsTests"/> to assert the resulting schema.
/// </summary>
public sealed class MigrationsFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    // `-h 127.0.0.1` forces the readiness probe over TCP. The
                    // official entrypoint runs initdb against a *temporary*
                    // server started with `listen_addresses=''`, so a
                    // socket-only `pg_isready -U postgres` greens for a window
                    // while nothing is listening on 5432 yet — Testcontainers
                    // then reports ready, docker's port proxy accepts the
                    // mapped-port connection and immediately closes it, and
                    // Npgsql surfaces "Attempted to read past the end of the
                    // stream" mid-authentication. Probing TCP skips that
                    // window (measured: socket ready ~4s before TCP).
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();

        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(ConnectionString);

        await ApplyMigrationsAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    // Mirrors DatabaseMigrationService: discover embedded Migrations/*.sql by the
    // assembly resource prefix, ordered ordinally by name, and apply each in order.
    private async Task ApplyMigrationsAsync()
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        const string prefix = "Verbara.Platform.Storage.Postgres.Migrations.";

        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        await using var conn = await DataSource.OpenConnectionAsync();
        foreach (var resourceName in resources)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Returns true if a column exists in the named table.</summary>
    public async Task<bool> TableExistsAsync(string table)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@Name) IS NOT NULL";
        cmd.Parameters.Add(new NpgsqlParameter("Name", table));
        var result = await cmd.ExecuteScalarAsync();
        return result is true;
    }

    /// <summary>Returns true if an index with the given name exists.</summary>
    public async Task<bool> IndexExistsAsync(string indexName)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = @Name)";
        cmd.Parameters.Add(new NpgsqlParameter("Name", indexName));
        var result = await cmd.ExecuteScalarAsync();
        return result is true;
    }

    /// <summary>Returns true if the named column exists on the named table.</summary>
    public async Task<bool> ColumnExistsAsync(string table, string column)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            "WHERE table_name = @Table AND column_name = @Column)";
        cmd.Parameters.Add(new NpgsqlParameter("Table", table));
        cmd.Parameters.Add(new NpgsqlParameter("Column", column));
        var result = await cmd.ExecuteScalarAsync();
        return result is true;
    }
}
