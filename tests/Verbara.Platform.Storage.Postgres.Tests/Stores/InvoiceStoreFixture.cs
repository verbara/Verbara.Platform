using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresInvoiceStoreTests"/>.
/// Spins up <c>postgres:16-alpine</c> and creates the <c>invoices</c> table in its
/// PRE-migration-011 shape (the squashed 001_Baseline.sql column set — no
/// <c>due_date</c>/<c>payment_status</c>), then applies the additive
/// <c>011_invoice_due_date_payment_status.sql</c> <c>ALTER TABLE</c> statements so the
/// migration's <c>SMALLINT NOT NULL DEFAULT 0</c> backfill is exercised exactly as it
/// runs in production. This lets the tests assert the real round-trip of the new
/// columns AND the legacy-row default (a row inserted before the ALTER is read back
/// with <c>PaymentStatus = Current</c> / <c>DueDate = null</c>).
/// </summary>
public sealed class InvoiceStoreFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres;SSL Mode=Disable";

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

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = BaselineSql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>
    /// Truncate + re-seed the <c>invoices</c> table between tests. Recreates the
    /// pre-migration baseline shape, inserts any caller-provided legacy rows via
    /// <paramref name="seedLegacyRows"/> (executed BEFORE the migration ALTER so the
    /// SMALLINT DEFAULT 0 backfill is observed), then applies migration 011.
    /// </summary>
    public async Task ResetAsync(string? seedLegacyRows = null)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE IF EXISTS invoices";
            await drop.ExecuteNonQueryAsync();
        }

        await using (var baseline = conn.CreateCommand())
        {
            baseline.CommandText = BaselineSql;
            await baseline.ExecuteNonQueryAsync();
        }

        if (seedLegacyRows is not null)
        {
            await using var seed = conn.CreateCommand();
            seed.CommandText = seedLegacyRows;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var migrate = conn.CreateCommand())
        {
            migrate.CommandText = Migration011Sql;
            await migrate.ExecuteNonQueryAsync();
        }
    }

    // invoices in its PRE-011 shape — mirrors the squashed 001_Baseline.sql block
    // (no due_date / payment_status). The fixture applies the additive 011 ALTER
    // separately so the migration's DEFAULT-0 backfill is part of the test surface.
    private const string BaselineSql = """
        CREATE TABLE IF NOT EXISTS invoices (
            invoice_id TEXT PRIMARY KEY,
            tenant_id TEXT NOT NULL,
            period_start TIMESTAMPTZ NOT NULL,
            period_end TIMESTAMPTZ NOT NULL,
            currency TEXT NOT NULL,
            line_items JSONB NOT NULL,
            subtotal NUMERIC(18,2) NOT NULL,
            tax NUMERIC(18,2) NOT NULL DEFAULT 0,
            total NUMERIC(18,2) NOT NULL,
            status SMALLINT NOT NULL DEFAULT 0,
            generated_at TIMESTAMPTZ NOT NULL,
            issued_at TIMESTAMPTZ,
            paid_at TIMESTAMPTZ
        );
        """;

    // Verbatim from migration 011_invoice_due_date_payment_status.sql.
    private const string Migration011Sql = """
        ALTER TABLE invoices
            ADD COLUMN IF NOT EXISTS due_date TIMESTAMPTZ;

        ALTER TABLE invoices
            ADD COLUMN IF NOT EXISTS payment_status SMALLINT NOT NULL DEFAULT 0;
        """;
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("InvoiceStore")]
public class InvoiceStoreCollection : ICollectionFixture<InvoiceStoreFixture>;
#pragma warning restore CA1711
