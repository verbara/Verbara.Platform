using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Verbara.Platform.Realtime.Contracts.Dtos;

namespace Verbara.Platform.Realtime.Services;

/// <summary>
/// Default <see cref="IRelayOutcomeSink"/>: a fixed-capacity in-memory ring
/// buffer + a <see cref="Counter{T}"/> instrument exported under the
/// <see cref="MeterName"/> meter so Prometheus / OpenTelemetry scrapes pick
/// up the relay's outcome stream alongside Pro.Cluster's
/// <c>ClusterLeadershipMetrics</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency:</b> writes from the relay's three Rx subscriptions race
/// against each other and against snapshot reads from the audit endpoint.
/// A single <see cref="Lock"/> serialises both — adequate because (a) each
/// recorded outcome is a struct field copy + counter increment, sub-µs, and
/// (b) audit snapshots are operator-grade traffic, not hot-path.
/// </para>
/// <para>
/// <b>Eviction:</b> when the ring is full the next write overwrites the
/// oldest slot and bumps <see cref="RelayOutcomePage.EvictedSinceStart"/>.
/// Callers polling <c>/admin/realtime/audit</c> use that counter to detect
/// that their <c>since</c> cursor predates the oldest retained entry, in
/// which case "0 forwards observed" would be a lie (the records existed but
/// were rolled out before the query landed).
/// </para>
/// </remarks>
internal sealed class RelayOutcomeSink : IRelayOutcomeSink, IDisposable
{
    /// <summary>Meter name subscribers should pass to <c>AddMeter</c>.</summary>
    public static readonly string MeterName = "Verbara.Platform.Realtime";

    private readonly Lock _gate = new();
    private readonly RelayOutcomeEntry?[] _buffer;
    private readonly int _capacity;
    private readonly string _podInstanceId;
    private readonly Meter _meter;
    private readonly Counter<long> _outcomes;
    private int _writeIndex;
    private int _currentSize;
    private long _evictedSinceStart;

    public RelayOutcomeSink(RelayOutcomeSinkOptions options, IMeterFactory? meterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Capacity < 100)
            throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be >= 100.");
        if (options.Capacity > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be <= 100,000.");

        _capacity = options.Capacity;
        _podInstanceId = options.PodInstanceId;
        _buffer = new RelayOutcomeEntry?[_capacity];

        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        _outcomes = _meter.CreateCounter<long>(
            "verbara_realtime_relay_outcomes_total",
            description: "PushToHubRelay outcomes tagged by outcome (Forwarded/SkippedNotLeader/SkippedNullTenant/SkippedNullNodeId/ForwardError), event_type, and leader-election resource.");
    }

    /// <inheritdoc/>
    public void Record(RelayOutcomeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // The sink is authoritative for PodInstanceId — callers (the relay) may
        // pass null because they don't know which pod they're running in; the
        // sink stamps the env-derived identity uniformly so the audit response
        // never carries a half-resolved row.
        var stamped = entry.PodInstanceId == _podInstanceId
            ? entry
            : entry with { PodInstanceId = _podInstanceId };

        lock (_gate)
        {
            if (_buffer[_writeIndex] is not null)
            {
                _evictedSinceStart++;
            }

            _buffer[_writeIndex] = stamped;
            _writeIndex = (_writeIndex + 1) % _capacity;

            if (_currentSize < _capacity)
            {
                _currentSize++;
            }
        }

        _outcomes.Add(
            1,
            new KeyValuePair<string, object?>("outcome", stamped.Outcome.ToString()),
            new KeyValuePair<string, object?>("event_type", stamped.EventType),
            new KeyValuePair<string, object?>("resource", stamped.Resource));
    }

    /// <inheritdoc/>
    public RelayOutcomePage Snapshot(DateTimeOffset? since, int limit)
    {
        if (limit <= 0)
        {
            limit = 1_000;
        }
        if (limit > _capacity)
        {
            limit = _capacity;
        }

        var entries = new List<RelayOutcomeEntry>(Math.Min(limit, 256));
        DateTimeOffset? oldest = null;
        int size;
        long evicted;

        lock (_gate)
        {
            size = _currentSize;
            evicted = _evictedSinceStart;

            if (_currentSize == 0)
            {
                return new RelayOutcomePage(_podInstanceId, _capacity, 0, evicted, null, entries);
            }

            var startIndex = (_writeIndex - _currentSize + _capacity) % _capacity;
            for (var i = 0; i < _currentSize; i++)
            {
                var slot = _buffer[(startIndex + i) % _capacity];
                if (slot is null)
                {
                    continue;
                }

                oldest ??= slot.Ts;

                if (since.HasValue && slot.Ts <= since.Value)
                {
                    continue;
                }

                entries.Add(slot);
                if (entries.Count >= limit)
                {
                    break;
                }
            }
        }

        return new RelayOutcomePage(
            PodInstanceId: _podInstanceId,
            Capacity: _capacity,
            CurrentSize: size,
            EvictedSinceStart: evicted,
            OldestRetainedTs: oldest,
            Entries: entries);
    }

    /// <inheritdoc/>
    public void Dispose() => _meter.Dispose();
}
