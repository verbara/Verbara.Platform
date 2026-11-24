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
/// <see cref="MigrationsTests"/> to assert the resulting schema. Runs on the floating
/// <c>postgres:16-alpine</c> tag, as it always has.
/// </summary>
public sealed class MigrationsFixture() : MigratedPostgresFixture("postgres:16-alpine");

/// <summary>
/// The same fixture on <c>postgres:18-alpine</c> pinned by digest — the image the product runs, at the digest the
/// routing skill catalog's statements were measured on (card PLT-SKL-01). The tests of
/// routing-skills-stay-in-their-tenant run here; every other test stays on <see cref="MigrationsFixture"/>.
/// </summary>
public sealed class PinnedPostgres18MigrationsFixture() : MigratedPostgresFixture(Image)
{
    /// <summary>The pinned image reference, by digest, never the tag.</summary>
    public const string Image =
        "postgres:18-alpine@sha256:0115267ab5a509022df9319c77be365cef335adb71556c75f60f5360ee247e71";
}

/// <summary>
/// A Testcontainers Postgres on <paramref name="image"/> with every embedded migration applied in name order, and
/// the schema probes the migration tests read.
/// </summary>
public abstract class MigratedPostgresFixture(string image) : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder(image)
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

    private const string MigrationResourcePrefix = "Verbara.Platform.Storage.Postgres.Migrations.";

    /// <summary>
    /// Runs one embedded migration (by file name, e.g. <c>023_WebhookDeliveryClaim.sql</c>) again, on top of the
    /// full set already applied — the check that a migration is idempotent.
    /// </summary>
    public async Task ReapplyMigrationAsync(string fileName)
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        await using var stream = assembly.GetManifestResourceStream(MigrationResourcePrefix + fileName)
            ?? throw new InvalidOperationException($"No embedded migration named {fileName}.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();

        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The column's type as Postgres prints it (<c>format_type</c>, e.g. <c>character varying(32)</c>) and whether
    /// it is nullable, or <c>null</c> when the column does not exist.
    /// </summary>
    public async Task<(string Type, bool Nullable)?> ColumnDefinitionAsync(string table, string column)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT format_type(a.atttypid, a.atttypmod), NOT a.attnotnull " +
            "FROM pg_attribute a " +
            "WHERE a.attrelid = to_regclass(@Table) AND a.attname = @Column AND a.attnum > 0 AND NOT a.attisdropped";
        cmd.Parameters.Add(new NpgsqlParameter("Table", table));
        cmd.Parameters.Add(new NpgsqlParameter("Column", column));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), reader.GetBoolean(1));
    }

    // Mirrors DatabaseMigrationService: discover embedded Migrations/*.sql by the
    // assembly resource prefix, ordered ordinally by name, and apply each in order.
    private async Task ApplyMigrationsAsync()
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        const string prefix = MigrationResourcePrefix;

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

    /// <summary>The columns of the table's primary key, in key order; empty when it has none.</summary>
    public async Task<IReadOnlyList<string>> PrimaryKeyColumnsAsync(string table)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT a.attname FROM pg_index i " +
            "CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) " +
            "JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum " +
            "WHERE i.indrelid = to_regclass(@Table) AND i.indisprimary ORDER BY k.ord";
        cmd.Parameters.Add(new NpgsqlParameter("Table", table));
        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns;
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
