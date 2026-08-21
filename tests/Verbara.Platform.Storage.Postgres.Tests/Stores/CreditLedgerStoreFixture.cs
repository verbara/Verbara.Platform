using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresCreditLedgerStoreTests"/>. Spins up
/// <c>postgres:16-alpine</c> and creates the <c>ai_credit_ledger</c> + <c>tenant_credit_balance</c> tables
/// and indexes VERBATIM from migration <c>012_credit_ledger.sql</c>, so the atomic guarded debit/grant
/// primitive — the <c>ON CONFLICT DO NOTHING</c> idempotency arbitration over the partial unique indexes,
/// the guarded <c>UPDATE … WHERE balance &gt;= @Amount</c>, and the ledger-SUM == projection-balance
/// invariant — is exercised against the real schema rather than the InMemory twin.
/// </summary>
public sealed class CreditLedgerStoreFixture : IAsyncLifetime
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

    /// <summary>Truncate both ledger tables between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "TRUNCATE ai_credit_ledger, tenant_credit_balance, credit_lot, credit_allocation, credit_lot_seq " +
            "RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Returns SUM(amount) over a tenant's ledger entries (0 when none) — the offline audit truth.</summary>
    public async Task<decimal> LedgerSumAsync(string tenantId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(amount), 0) FROM ai_credit_ledger WHERE tenant_id = @t";
        cmd.Parameters.Add(new NpgsqlParameter("t", tenantId));
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? 0m : (decimal)result;
    }

    /// <summary>Returns the count of ledger rows for a tenant (for idempotency assertions).</summary>
    public async Task<long> LedgerRowCountAsync(string tenantId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ai_credit_ledger WHERE tenant_id = @t";
        cmd.Parameters.Add(new NpgsqlParameter("t", tenantId));
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : (long)result;
    }

    /// <summary>Returns the count of debit rows for a tenant tagged with a given <c>source</c> ordinal.</summary>
    public async Task<long> DebitRowCountBySourceAsync(string tenantId, short source)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM ai_credit_ledger WHERE tenant_id = @t AND entry_type = 1 AND source = @s";
        cmd.Parameters.Add(new NpgsqlParameter("t", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("s", source));
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : (long)result;
    }

    /// <summary>
    /// Returns the count of <c>credit_allocation</c> rows whose <c>debit_entry_id</c> belongs to one of the
    /// tenant's ledger debit rows — the internal debit→lot linkage count for a tenant.
    /// </summary>
    public async Task<long> AllocationRowCountAsync(string tenantId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM credit_allocation a " +
            "WHERE a.debit_entry_id IN (SELECT entry_id FROM ai_credit_ledger WHERE tenant_id = @t)";
        cmd.Parameters.Add(new NpgsqlParameter("t", tenantId));
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : (long)result;
    }

    /// <summary>
    /// Returns SUM(amount) over the <c>credit_allocation</c> rows whose <c>debit_entry_id</c> belongs to one of
    /// the tenant's ledger debit rows (0 when none) — the internal debit→lot draw total for a tenant.
    /// </summary>
    public async Task<decimal> AllocationAmountSumAsync(string tenantId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COALESCE(SUM(a.amount), 0) FROM credit_allocation a " +
            "WHERE a.debit_entry_id IN (SELECT entry_id FROM ai_credit_ledger WHERE tenant_id = @t)";
        cmd.Parameters.Add(new NpgsqlParameter("t", tenantId));
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? 0m : (decimal)result;
    }

    // DDL — mirrors ai_credit_ledger + tenant_credit_balance in 012_credit_ledger.sql VERBATIM.
    private const string SchemaSql = """
        CREATE TABLE ai_credit_ledger (
            entry_id        TEXT PRIMARY KEY,
            tenant_id       TEXT NOT NULL,
            entry_type      SMALLINT NOT NULL,
            source          SMALLINT NOT NULL,
            amount          NUMERIC(18,6) NOT NULL,
            period_key      TEXT,
            external_ref    TEXT,
            expires_at      TIMESTAMPTZ,
            usage_record_id TEXT,
            created_at      TIMESTAMPTZ NOT NULL
        );

        CREATE INDEX idx_ai_credit_ledger_tenant_created
            ON ai_credit_ledger (tenant_id, created_at);

        CREATE UNIQUE INDEX uq_ai_credit_ledger_period
            ON ai_credit_ledger (tenant_id, period_key, entry_type)
            WHERE period_key IS NOT NULL;

        CREATE UNIQUE INDEX uq_ai_credit_ledger_extref
            ON ai_credit_ledger (tenant_id, external_ref)
            WHERE external_ref IS NOT NULL;

        CREATE TABLE tenant_credit_balance (
            tenant_id  TEXT PRIMARY KEY,
            balance    NUMERIC(18,6) NOT NULL,
            version    BIGINT NOT NULL,
            updated_at TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE credit_lot (
            lot_id      TEXT PRIMARY KEY,
            tenant_id   TEXT NOT NULL,
            source      SMALLINT NOT NULL,
            original    NUMERIC(18,6) NOT NULL,
            remaining   NUMERIC(18,6) NOT NULL CHECK (remaining >= 0),
            expires_at  TIMESTAMPTZ,
            granted_at  TIMESTAMPTZ NOT NULL,
            lot_seq     BIGINT NOT NULL
        );

        CREATE TABLE credit_allocation (
            allocation_id  TEXT PRIMARY KEY,
            debit_entry_id TEXT NOT NULL,
            lot_id         TEXT NOT NULL,
            source         SMALLINT NOT NULL,
            amount         NUMERIC(18,6) NOT NULL,
            created_at     TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE credit_lot_seq (
            tenant_id TEXT PRIMARY KEY,
            next_seq  BIGINT NOT NULL
        );

        CREATE INDEX idx_credit_lot_fifo
            ON credit_lot (tenant_id, source, expires_at, granted_at, lot_seq);

        CREATE INDEX idx_credit_allocation_debit
            ON credit_allocation (debit_entry_id);

        CREATE INDEX idx_credit_allocation_lot
            ON credit_allocation (lot_id);
        """;
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("CreditLedgerStore")]
public class CreditLedgerStoreCollection : ICollectionFixture<CreditLedgerStoreFixture>;
#pragma warning restore CA1711
