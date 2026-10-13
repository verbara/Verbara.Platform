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
    /// <summary>
    /// <c>Npgsql</c> publishes the pool instruments (<c>db.client.connection.count</c>,
    /// <c>db.client.connection.max</c>, <c>db.client.connection.npgsql.pending_requests</c>, …) and
    /// <c>db.client.operation.duration</c>; Npgsql 10 needs no opt-in. <see cref="AuthWriteQueue.MeterName"/>
    /// publishes <c>auth.write.{enqueued,dropped,processed,failed}</c>.
    /// </summary>
    public static IReadOnlyList<string> MeterNames { get; } =
    [
        "Npgsql",
        AuthWriteQueue.MeterName,
    ];

    /// <summary>Enrols every meter in <see cref="MeterNames"/> on the host's OpenTelemetry builder.</summary>
    public static VerbaraOpenTelemetryBuilder AddCapacityMeters(this VerbaraOpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var meterName in MeterNames)
            builder.AddMeter(meterName);
        return builder;
    }
}
