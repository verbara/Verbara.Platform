using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresDialerLicenseAuditSinkTests"/>
/// (dialer-license-audit-sink, Pro/ADR-0016). Spins up <c>postgres:16-alpine</c> and applies the
/// REAL shipped <c>017_DialerLicenseAudit.sql</c> migration from disk, so the sink's INSERT is
/// exercised against the actual <c>dialer_license_audit</c> column shape (not a hand-copied DDL) —
/// the live-DB lane (<c>live-db-ci-lane</c>) the InMemory mirror cannot reach.
/// </summary>
public sealed class DialerLicenseAuditFixture : IAsyncLifetime
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
                Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "postgres"))
            .Build();

        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(ConnectionString);

        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ReadMigration();
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Truncate the audit table between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE dialer_license_audit RESTART IDENTITY";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Reads the single most-recent audit row directly for column-level assertions.</summary>
    public async Task<AuditRowSnapshot> ReadSingleRowAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT schema_version, event, occurred_at, tick_sequence, engine_instance_id, " +
            "reason, reason_sequence, consecutive_blocked_ticks, campaigns::text AS campaigns, " +
            "in_flight_at_quiesce, license_id, licensee, tier, campaigns_rebuilt " +
            "FROM dialer_license_audit ORDER BY id DESC LIMIT 1";
        await using var reader = await cmd.ExecuteReaderAsync();
        var read = await reader.ReadAsync();
        if (!read)
            throw new InvalidOperationException("Expected exactly one dialer_license_audit row.");

        return new AuditRowSnapshot
        {
            SchemaVersion = reader.GetInt32(reader.GetOrdinal("schema_version")),
            Event = reader.GetString(reader.GetOrdinal("event")),
            OccurredAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("occurred_at")),
            TickSequence = reader.GetInt64(reader.GetOrdinal("tick_sequence")),
            EngineInstanceId = reader.GetGuid(reader.GetOrdinal("engine_instance_id")),
            Reason = ReadStringOrNull(reader, "reason"),
            ReasonSequence = ReadStringOrNull(reader, "reason_sequence"),
            ConsecutiveBlockedTicks = reader.GetInt32(reader.GetOrdinal("consecutive_blocked_ticks")),
            CampaignsJson = reader.GetString(reader.GetOrdinal("campaigns")),
            InFlightAtQuiesce = reader.GetInt32(reader.GetOrdinal("in_flight_at_quiesce")),
            LicenseId = ReadStringOrNull(reader, "license_id"),
            Licensee = ReadStringOrNull(reader, "licensee"),
            Tier = reader.GetString(reader.GetOrdinal("tier")),
            CampaignsRebuilt = reader.GetInt32(reader.GetOrdinal("campaigns_rebuilt")),
        };
    }

    public async Task<long> CountRowsAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dialer_license_audit";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static string? ReadStringOrNull(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string ReadMigration()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "Verbara.Platform.Storage.Postgres", "Migrations", "017_DialerLicenseAudit.sql"));
        return File.ReadAllText(path);
    }

    /// <summary>A read-back snapshot of one <c>dialer_license_audit</c> row (column names verbatim).</summary>
    public sealed class AuditRowSnapshot
    {
        public required int SchemaVersion { get; init; }
        public required string Event { get; init; }
        public required DateTimeOffset OccurredAt { get; init; }
        public required long TickSequence { get; init; }
        public required Guid EngineInstanceId { get; init; }
        public required string? Reason { get; init; }
        public required string? ReasonSequence { get; init; }
        public required int ConsecutiveBlockedTicks { get; init; }
        public required string CampaignsJson { get; init; }
        public required int InFlightAtQuiesce { get; init; }
        public required string? LicenseId { get; init; }
        public required string? Licensee { get; init; }
        public required string Tier { get; init; }
        public required int CampaignsRebuilt { get; init; }
    }
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("DialerLicenseAudit")]
public sealed class DialerLicenseAuditCollection : ICollectionFixture<DialerLicenseAuditFixture>;
#pragma warning restore CA1711
