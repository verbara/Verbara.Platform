#if MRB_PENDING
using System.Diagnostics.Metrics;
using Npgsql;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests.Observability;

/// <summary>
/// measure-api-multi-replica-baseline (D2): <see cref="DatabaseMigrationService.ApplyMigrations"/> builds
/// its own data source. Npgsql reports a data source's pool on <c>/metrics</c> from construction until
/// it is disposed, so one the runner leaks keeps an unnamed pool, labelled with its connection string,
/// on every scrape for the life of the process. No database: the connection is refused. Guarded by
/// <c>MRB_PENDING</c> until the runner disposes its source (task 2.2 removes the guard).
/// </summary>
public sealed class DatabaseMigrationServiceDataSourceTests
{
    [Fact]
    public void ApplyMigrations_ShouldReleaseItsDataSource_WhenConnectionFails()
    {
        var marker = Guid.NewGuid().ToString("N");

        var act = () => DatabaseMigrationService.ApplyMigrations(
            $"Host=127.0.0.1;Port=1;Timeout=1;Application Name={marker}");

        act.Should().Throw<NpgsqlException>();
        PoolTagsReportedNow().Should().NotContain(tag => tag.Contains(marker, StringComparison.Ordinal),
            "the runner's data source must be disposed even when its connection fails");
    }

    /// <summary>The pool-name tags of every observable Npgsql measurement reported right now.</summary>
    private static List<string> PoolTagsReportedNow()
    {
        var poolTags = new List<string>();
        var gate = new Lock();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Npgsql")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) => Collect(tags));
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Collect(tags));
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Collect(tags));
        listener.Start();
        listener.RecordObservableInstruments();

        lock (gate)
            return [.. poolTags];

        void Collect(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "db.client.connection.pool.name" && tag.Value is string pool)
                {
                    lock (gate)
                        poolTags.Add(pool);
                }
            }
        }
    }
}
#endif
