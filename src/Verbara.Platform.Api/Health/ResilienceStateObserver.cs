using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Verbara.Sdk.Resilience.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// Listens to the <c>Verbara.Sdk.Resilience</c> meter's <c>circuit.state</c>
/// observable gauge and maintains an in-memory snapshot of per-policy-key circuit
/// state plus a timestamp of when the current state was first observed.
/// </summary>
/// <remarks>
/// <para>
/// The SDK <c>ResiliencePolicy</c> does not expose per-key circuit state through
/// its public surface; the only supported observation vector is the
/// <see cref="ResilienceMetrics.CircuitStateGauge"/> (observable gauge). We use
/// <see cref="MeterListener"/> to probe the gauge on a periodic tick, caching
/// the most recent values so health-check implementations can consult them
/// synchronously without allocating.
/// </para>
/// <para>
/// This observer is per-process. In multi-instance deployments, each instance
/// only sees circuits opened on its own process — that matches the intended
/// "this pod's health" semantics of ASP.NET Core health checks. Cross-instance
/// visibility should flow through the OpenTelemetry/Prometheus pipeline, not
/// through this observer.
/// </para>
/// </remarks>
internal sealed class ResilienceStateObserver : IHostedService, IResilienceStateObserver, IDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private readonly ConcurrentDictionary<string, ResilienceCircuitSnapshot> _snapshots = new(StringComparer.Ordinal);
    private MeterListener? _listener;
    private ITimer? _timer;

    public ResilienceStateObserver(TimeProvider? time = null, TimeSpan? pollInterval = null)
    {
        _time = time ?? TimeProvider.System;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ResilienceMetrics.MeterName
                    && instrument.Name == "circuit.state")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<int>(OnMeasurement);
        _listener.Start();

        // Prime the cache immediately so early health-check probes see current state.
        _listener.RecordObservableInstruments();

        _timer = _time.CreateTimer(_ =>
        {
            try
            {
                _listener?.RecordObservableInstruments();
            }
            catch
            {
                // A missing instrument (e.g., during shutdown) must not crash the timer callback.
            }
        }, state: null, dueTime: _pollInterval, period: _pollInterval);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        _listener?.Dispose();
        _listener = null;
        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<string, ResilienceCircuitSnapshot> GetAll()
    {
        // Defensive copy — consumers can enumerate without racing with updates.
        return new Dictionary<string, ResilienceCircuitSnapshot>(_snapshots, StringComparer.Ordinal);
    }

    public ResilienceCircuitSnapshot? GetSnapshot(string policyKey)
    {
        ArgumentNullException.ThrowIfNull(policyKey);
        return _snapshots.TryGetValue(policyKey, out var snapshot) ? snapshot : null;
    }

    /// <summary>
    /// Test hook: seed a known circuit state without going through the MeterListener.
    /// </summary>
    internal void SetForTest(string policyKey, ResilienceCircuitState state, DateTimeOffset stateSince)
    {
        _snapshots[policyKey] = new ResilienceCircuitSnapshot(policyKey, state, stateSince);
    }

    private void OnMeasurement(
        Instrument instrument,
        int measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        string? key = null;
        foreach (var tag in tags)
        {
            if (tag.Key == "key" && tag.Value is string s)
            {
                key = s;
                break;
            }
        }

        if (key is null)
            return;

        var newState = measurement switch
        {
            (int)ResilienceMetrics.CircuitStateValue.Open => ResilienceCircuitState.Open,
            (int)ResilienceMetrics.CircuitStateValue.HalfOpen => ResilienceCircuitState.HalfOpen,
            _ => ResilienceCircuitState.Closed,
        };

        var now = _time.GetUtcNow();
        _snapshots.AddOrUpdate(
            key,
            _ => new ResilienceCircuitSnapshot(key, newState, now),
            (_, existing) => existing.State == newState
                ? existing
                : new ResilienceCircuitSnapshot(key, newState, now));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _listener?.Dispose();
    }
}
