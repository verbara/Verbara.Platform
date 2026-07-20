using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Verbara.Sdk.Data.Npgsql;
using Verbara.Sdk.Pro.Dialer.Diagnostics;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres implementation of the optional Pro seam <see cref="IDialerLicenseAuditSink"/>
/// (dialer-license-audit-sink, decision_ref Pro/ADR-0016). Persists each tick-scoped
/// license-enforcement episode the Pro <c>DialerEngine</c> delivers
/// (<see cref="DialerLicenseAuditRecord"/>) into the <c>dialer_license_audit</c> table
/// (migration 017) — turning a silently-dropped record into a durable compliance row.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the canonical <see cref="PostgresAuditStore"/>: one <c>NpgsqlDataSource.ExecuteAsync</c>
/// INSERT via <c>Verbara.Sdk.Data.Npgsql</c> (NO Dapper — Platform/ADR-0022), every field bound as an
/// explicit <see cref="NpgsqlParameter"/>. The four nullable columns (<c>reason</c>,
/// <c>reason_sequence</c>, <c>license_id</c>, <c>licensee</c>) bind
/// <c>(object?)value ?? DBNull.Value</c> with an explicit <see cref="NpgsqlDbType.Text"/> so Postgres
/// never throws <c>42P08</c> (indeterminate parameter type). The <c>Event</c>, <c>Reason</c>, and
/// <c>Tier</c> enums persist as text (<c>.ToString()</c>); the <see cref="DialerLicenseAuditRecord.Campaigns"/>
/// snapshot serializes through the <see cref="PostgresJson.Ctx"/> source-gen context (no reflection) and
/// binds as a string param with the <c>::jsonb</c> SQL cast (design D1, D2).
/// </para>
/// <para>
/// Fail-safe (design D5, spec Requirement 3): the seam contract states the sink MUST NOT throw into the
/// dial path. <see cref="RecordAsync"/> therefore wraps the INSERT in a try/catch that logs the fault
/// and returns normally — a transient DB outage degrades to a missing (logged) audit row, never to a
/// disrupted dial loop. The Pro engine also invokes the sink try/caught; honoring the contract here is
/// defense in depth.
/// </para>
/// </remarks>
internal sealed partial class PostgresDialerLicenseAuditSink : IDialerLicenseAuditSink
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresDialerLicenseAuditSink> _logger;

    public PostgresDialerLicenseAuditSink(
        NpgsqlDataSource dataSource,
        ILogger<PostgresDialerLicenseAuditSink> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _logger = logger;
    }

    public async ValueTask RecordAsync(DialerLicenseAuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        // The Campaigns snapshot persists as jsonb through the source-gen context (no reflection),
        // exactly as PostgresAuditStore serializes Metadata/Before/After (design D2, D6).
        var campaignsJson = JsonSerializer.Serialize(
            record.Campaigns, PostgresJson.Ctx.IReadOnlyListQuiescedCampaignInfo);

        try
        {
            await _dataSource.ExecuteAsync(
                "INSERT INTO dialer_license_audit (" +
                "schema_version, event, occurred_at, tick_sequence, engine_instance_id, " +
                "reason, reason_sequence, consecutive_blocked_ticks, campaigns, in_flight_at_quiesce, " +
                "license_id, licensee, tier, campaigns_rebuilt) " +
                "VALUES (" +
                "@SchemaVersion, @Event, @OccurredAt, @TickSequence, @EngineInstanceId, " +
                "@Reason, @ReasonSequence, @ConsecutiveBlockedTicks, @Campaigns::jsonb, @InFlightAtQuiesce, " +
                "@LicenseId, @Licensee, @Tier, @CampaignsRebuilt)",
                p =>
                {
                    p.Add(new NpgsqlParameter("SchemaVersion", record.SchemaVersion));
                    p.Add(new NpgsqlParameter("Event", record.Event.ToString()));
                    p.Add(new NpgsqlParameter("OccurredAt", record.OccurredAt));
                    p.Add(new NpgsqlParameter("TickSequence", record.TickSequence));
                    p.Add(new NpgsqlParameter("EngineInstanceId", record.EngineInstanceId));
                    // Nullable enum: null Reason (e.g. Recovered) binds SQL NULL; explicit Text type
                    // avoids 42P08.
                    p.Add(new NpgsqlParameter("Reason", NpgsqlDbType.Text)
                    {
                        Value = (object?)record.Reason?.ToString() ?? DBNull.Value,
                    });
                    p.Add(new NpgsqlParameter("ReasonSequence", NpgsqlDbType.Text)
                    {
                        Value = (object?)record.ReasonSequence ?? DBNull.Value,
                    });
                    p.Add(new NpgsqlParameter("ConsecutiveBlockedTicks", record.ConsecutiveBlockedTicks));
                    p.Add(new NpgsqlParameter("Campaigns", campaignsJson));
                    p.Add(new NpgsqlParameter("InFlightAtQuiesce", record.InFlightAtQuiesce));
                    p.Add(new NpgsqlParameter("LicenseId", NpgsqlDbType.Text)
                    {
                        Value = (object?)record.LicenseId ?? DBNull.Value,
                    });
                    p.Add(new NpgsqlParameter("Licensee", NpgsqlDbType.Text)
                    {
                        Value = (object?)record.Licensee ?? DBNull.Value,
                    });
                    p.Add(new NpgsqlParameter("Tier", record.Tier.ToString()));
                    p.Add(new NpgsqlParameter("CampaignsRebuilt", record.CampaignsRebuilt));
                },
                ct);
        }
        catch (Exception ex)
        {
            // Contract: "Must not throw into the dial path" (DialerLicenseAudit.cs, design D5). A
            // persistence fault degrades to a missing (logged) audit row, never a disrupted dial loop.
            LogPersistFailed(_logger, record.Event.ToString(), record.EngineInstanceId, record.TickSequence, ex);
        }
    }

    [LoggerMessage(EventId = 4120, Level = LogLevel.Error,
        Message = "Failed to persist dialer license audit record (event {Event}, engine {EngineInstanceId}, tick {TickSequence}); the record was dropped so the dial path is unaffected")]
    private static partial void LogPersistFailed(
        ILogger logger, string @event, Guid engineInstanceId, long tickSequence, Exception ex);
}
