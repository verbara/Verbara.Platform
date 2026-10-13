using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.OpenTelemetry;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// measure-api-multi-replica-baseline D1 — the saturation meters the Sdk and Pro OpenTelemetry lists leave
/// out, enrolled by the Api itself so <c>/metrics</c> shows how close a replica is to its limits: the
/// Postgres connection pool and operation instruments, and the auth write queue. Enrolment is by meter
/// name only (a string filter), so it is Native AOT-safe.
/// </summary>
public static class CapacityMetersExtensions
{
    private const string NpgsqlMeterName = "Npgsql";

    /// <summary>
    /// <c>Npgsql</c> publishes the pool instruments (<c>db.client.connection.count</c>,
    /// <c>db.client.connection.max</c>, <c>db.client.connection.npgsql.pending_requests</c>, …) and
    /// <c>db.client.operation.duration</c>; Npgsql 10 needs no opt-in. <see cref="AuthWriteQueue.MeterName"/>
    /// publishes <c>auth.write.{enqueued,dropped,processed,failed}</c>.
    /// </summary>
    public static IReadOnlyList<string> MeterNames { get; } =
    [
        NpgsqlMeterName,
        AuthWriteQueue.MeterName,
    ];

    /// <summary>
    /// The only tags an <c>Npgsql</c> series keeps on the unauthenticated <c>/metrics</c> (owner decision
    /// 2026-10-13, measure-api-multi-replica-baseline D2): the pool's fixed name, the connection state the
    /// pool-utilization alert selects, and the database system. Npgsql 10 also tags
    /// <c>db.client.operation.duration</c> with <c>server.address</c> and <c>server.port</c>, the Postgres
    /// host and port, whatever the pool's name. An allow-list rather than a deny-list, so a tag a later
    /// Npgsql adds stays off the endpoint until it is listed here on purpose.
    /// </summary>
    private static readonly string[] NpgsqlTagKeys =
    [
        "db.client.connection.pool.name",
        "db.client.connection.state",
        "db.system.name",
    ];

    /// <summary>
    /// Enrols every meter in <see cref="MeterNames"/> on the host's OpenTelemetry builder, and keeps only
    /// <see cref="NpgsqlTagKeys"/> on every <c>Npgsql</c> instrument it exports.
    /// </summary>
    public static VerbaraOpenTelemetryBuilder AddCapacityMeters(this VerbaraOpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var meterName in MeterNames)
            builder.AddMeter(meterName);
        return builder.ConfigureMetrics(static metrics => metrics.AddView(NpgsqlTagView));
    }

    /// <summary>
    /// The view for <see cref="NpgsqlTagKeys"/>. A plain <see cref="MetricStreamConfiguration"/> keeps a
    /// histogram's bucket advice, so <c>db_client_operation_duration_seconds</c> keeps Npgsql's
    /// <c>le="0.5"</c> boundary, which the slow-query alert reads. Any other meter's instrument gets
    /// <see langword="null"/>, i.e. the default stream.
    /// </summary>
    private static MetricStreamConfiguration? NpgsqlTagView(Instrument instrument) =>
        instrument.Meter.Name == NpgsqlMeterName
            ? new MetricStreamConfiguration { TagKeys = NpgsqlTagKeys }
            : null;
}
