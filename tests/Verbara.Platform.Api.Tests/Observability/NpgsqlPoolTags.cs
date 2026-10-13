using System.Diagnostics.Metrics;

namespace Verbara.Platform.Api.Tests.Observability;

/// <summary>
/// Reads the <c>db.client.connection.pool.name</c> tag of every observable <c>Npgsql</c> measurement in
/// this process. Npgsql reports a data source's pool from its construction until it is disposed, so the
/// result is the set of pools alive right now, whoever built them.
/// </summary>
internal static class NpgsqlPoolTags
{
    public static List<string> ReportedNow()
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
