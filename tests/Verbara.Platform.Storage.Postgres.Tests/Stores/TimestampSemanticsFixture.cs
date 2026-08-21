using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresTimestampSemanticsTests"/>.
/// Spins up <c>postgres:16-alpine</c> with the <c>canned_responses</c> DDL mirrored from
/// migration <c>001_Baseline.sql</c> so the <c>timestamptz</c> read/write contract
/// (design D1/D2 of <c>fix-local-kind-datetimeoffset</c>) can be exercised against a real
/// database — both the <c>new DateTimeOffset(x, TimeSpan.Zero)</c> projection in
/// <c>PostgresCannedResponseStore</c> and a raw <see cref="DateTimeOffset"/> parameter bind.
/// </summary>
public sealed class TimestampSemanticsFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

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

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Truncate the canned_responses table between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE canned_responses RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    // DDL — mirrors canned_responses in 001_Baseline.sql. Both temporal columns are
    // TIMESTAMPTZ (created_at NOT NULL, updated_at nullable), which is the exact shape
    // the read-side projection and the write-side converter contract are about.
    private const string SchemaSql = """
        CREATE TABLE canned_responses (
            response_id TEXT NOT NULL,
            tenant_id   TEXT NOT NULL,
            shortcut    TEXT NOT NULL,
            title       TEXT NOT NULL,
            body        TEXT NOT NULL,
            category    TEXT,
            tags        TEXT,
            created_by  TEXT NOT NULL,
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at  TIMESTAMPTZ,
            PRIMARY KEY (tenant_id, response_id)
        );

        CREATE UNIQUE INDEX ix_canned_responses_shortcut
            ON canned_responses (tenant_id, shortcut);
        """;
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("TimestampSemantics")]
public class TimestampSemanticsCollection : ICollectionFixture<TimestampSemanticsFixture>;
#pragma warning restore CA1711
