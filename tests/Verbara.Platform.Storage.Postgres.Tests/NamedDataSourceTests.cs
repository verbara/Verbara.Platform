#if MRB_PENDING
using System.Diagnostics.Metrics;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests;

/// <summary>
/// measure-api-multi-replica-baseline (D2, D5): a data source built by <see cref="NamedDataSource"/>
/// publishes the Npgsql pool and operation instruments under the name it was given, never under its
/// connection string. Pins the instrument names the shipped alert rules read, so an Npgsql upgrade that
/// renames them fails this lane instead of silencing the alerts. Guarded by <c>MRB_PENDING</c> until the
/// helper exists (task 2.2 removes the guard).
/// </summary>
public sealed class NamedDataSourceTests : IClassFixture<NamedDataSourceFixture>
{
    private readonly NamedDataSourceFixture _fixture;

    public NamedDataSourceTests(NamedDataSourceFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Build_ShouldPublishPoolInstrumentsUnderItsName_WhenSourceIsOpened()
    {
        var name = $"mrb-{Guid.NewGuid():N}";
        var marker = Guid.NewGuid().ToString("N");
        using var recorder = new NpgsqlMeterRecorder();

        await using (var dataSource = NamedDataSource.Build(_fixture.ConnectionString(marker), name))
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using (var command = new NpgsqlCommand("SELECT 1", connection))
                await command.ExecuteScalarAsync();

            recorder.RecordObservableInstruments();
        }

        recorder.PublishedInstruments().Should().Contain(
            [
                "db.client.connection.count",
                "db.client.connection.max",
                "db.client.connection.npgsql.pending_requests",
                "db.client.operation.duration",
            ],
            "these are the instruments the pool and slow-query alert rules read");
        recorder.InstrumentsMeasuredFor(name).Should().Contain(
            ["db.client.connection.count", "db.client.connection.max", "db.client.operation.duration"],
            "the pool and the command it ran are reported under the given name");

        var poolTagsOfThisSource = recorder.PoolTags()
            .Where(tag => tag == name || tag.Contains(marker, StringComparison.Ordinal))
            .ToList();
        poolTagsOfThisSource.Should().OnlyContain(tag => tag == name,
            "a pool that falls back to its connection string would put it on an unauthenticated endpoint");
        poolTagsOfThisSource.Should().NotContain(tag =>
            tag.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("Username=", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_ShouldReportPendingRequests_WhenPoolIsExhausted()
    {
        var name = $"mrb-{Guid.NewGuid():N}";
        using var recorder = new NpgsqlMeterRecorder();
        await using var dataSource = NamedDataSource.Build(
            _fixture.ConnectionString(Guid.NewGuid().ToString("N")) + ";Maximum Pool Size=1", name);

        var held = await dataSource.OpenConnectionAsync();
        var waiting = dataSource.OpenConnectionAsync().AsTask();
        long pendingWhileExhausted;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (recorder.PendingRequests(name) < 1 && !waiting.IsCompleted && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            pendingWhileExhausted = recorder.PendingRequests(name);
        }
        finally
        {
            await held.DisposeAsync();
            await (await waiting).DisposeAsync();
        }

        pendingWhileExhausted.Should().BeGreaterThanOrEqualTo(1,
            "a request waiting on an exhausted pool must be visible on that pool's series");
    }

    /// <summary>
    /// Records every measurement of the <c>Npgsql</c> meter in this process. Other tests run their own
    /// data sources in parallel, so every reading is filtered by pool name.
    /// </summary>
    private sealed class NpgsqlMeterRecorder : IDisposable
    {
        private const string PoolNameTag = "db.client.connection.pool.name";

        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly HashSet<string> _published = new(StringComparer.Ordinal);
        private readonly List<(string Instrument, string? Pool, double Value)> _measurements = [];

        public NpgsqlMeterRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name != "Npgsql")
                    return;
                lock (_gate)
                    _published.Add(instrument.Name);
                listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();
        }

        public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

        public IReadOnlyList<string> PublishedInstruments()
        {
            lock (_gate)
                return [.. _published];
        }

        public IReadOnlyList<string> InstrumentsMeasuredFor(string poolName)
        {
            lock (_gate)
                return [.. _measurements.Where(m => m.Pool == poolName).Select(m => m.Instrument).Distinct()];
        }

        public IReadOnlyList<string> PoolTags()
        {
            lock (_gate)
                return [.. _measurements.Select(m => m.Pool).OfType<string>().Distinct()];
        }

        /// <summary>The running sum of the pending-requests up-down counter for one pool.</summary>
        public long PendingRequests(string poolName)
        {
            lock (_gate)
                return (long)_measurements
                    .Where(m => m.Instrument == "db.client.connection.npgsql.pending_requests" && m.Pool == poolName)
                    .Sum(m => m.Value);
        }

        public void Dispose() => _listener.Dispose();

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? pool = null;
            foreach (var tag in tags)
            {
                if (tag.Key == PoolNameTag)
                    pool = tag.Value as string;
            }

            lock (_gate)
                _measurements.Add((instrument.Name, pool, value));
        }
    }
}
#endif
