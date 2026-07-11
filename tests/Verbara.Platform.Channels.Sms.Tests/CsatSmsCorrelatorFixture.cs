using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Channels.Sms.Tests;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="CsatSmsCorrelatorTests"/>. Spins up
/// <c>postgres:16-alpine</c> with the <c>csat_pending_dispatches</c> DDL mirrored from migration
/// <c>016_SurveyCsatExtensions.sql</c> (including the open-dispatch partial index) so the correlator's
/// 24h-window, collision, and fall-through logic can be exercised against a real database.
/// </summary>
public sealed class CsatSmsCorrelatorFixture : IAsyncLifetime
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
                Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "postgres"))
            .Build();

        await _container.StartAsync();

        // pg_isready returns ready before Postgres reliably accepts real connections (the
        // post-handshake "Connection reset by peer" race the repo's other Testcontainers fixtures
        // hit under host contention). Retry the first real connection so schema creation — and every
        // test after it — runs against a genuinely connectable server.
        await OpenWithRetryAndCreateSchemaAsync();
    }

    private async Task OpenWithRetryAndCreateSchemaAsync()
    {
        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(ConnectionString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = SchemaSql;
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (NpgsqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Truncate csat_pending_dispatches between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE csat_pending_dispatches";
        await cmd.ExecuteNonQueryAsync();
    }

    // DDL — mirrors csat_pending_dispatches in 016_SurveyCsatExtensions.sql (incl. the open-dispatch index).
    private const string SchemaSql = """
        CREATE TABLE csat_pending_dispatches (
            dispatch_id     TEXT        NOT NULL,
            tenant_id       TEXT        NOT NULL,
            channel         TEXT        NOT NULL,
            correlator      TEXT        NOT NULL,
            survey_id       TEXT        NOT NULL,
            queue_name      TEXT        NULL,
            conversation_id TEXT        NULL,
            sent_at         TIMESTAMPTZ NOT NULL,
            expires_at      TIMESTAMPTZ NOT NULL,
            consumed_at     TIMESTAMPTZ NULL,
            PRIMARY KEY (tenant_id, dispatch_id),
            CONSTRAINT chk_csat_pending_dispatches_channel CHECK (channel IN ('email','sms'))
        );

        CREATE INDEX idx_csat_pending_open
            ON csat_pending_dispatches (tenant_id, channel, correlator, sent_at DESC)
            WHERE consumed_at IS NULL;
        """;
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("CsatSmsCorrelator")]
public class CsatSmsCorrelatorCollection : ICollectionFixture<CsatSmsCorrelatorFixture>;
#pragma warning restore CA1711
