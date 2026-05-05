using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Health;

internal sealed class BackgroundServiceHealthCheck : IHealthCheck
{
    private readonly IServiceHeartbeat _heartbeat;
    private readonly IResilienceStateObserver _observer;
    private readonly PlatformHealthCheckOptions _healthOptions;

    public BackgroundServiceHealthCheck(
        IServiceHeartbeat heartbeat,
        IResilienceStateObserver observer,
        IOptions<PlatformHealthCheckOptions> healthOptions)
    {
        _heartbeat = heartbeat;
        _observer = observer;
        _healthOptions = healthOptions.Value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var heartbeats = _heartbeat.GetAll();
        var unhealthyHeartbeats = heartbeats.Where(kv => !kv.Value.IsHealthy).ToList();

        var (circuitStatus, circuitMessage, circuitData) = AsteriskAmiHealthCheck.EvaluateCircuits(
            _healthOptions.WorkerWatchedPolicyKeys,
            _healthOptions.UnhealthyOpenThreshold,
            _observer);

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["heartbeats"] = heartbeats.ToDictionary(
                kv => kv.Key,
                kv => (object)new
                {
                    kv.Value.IsHealthy,
                    lastTick = kv.Value.LastTick,
                    expectedIntervalSeconds = Math.Round(kv.Value.ExpectedInterval.TotalSeconds, 1),
                },
                StringComparer.Ordinal),
            ["circuits"] = circuitData,
        };

        // Heartbeat dominates — a stalled worker is always Unhealthy regardless of circuits.
        if (unhealthyHeartbeats.Count > 0)
        {
            var names = string.Join(", ", unhealthyHeartbeats.Select(kv => kv.Key));
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Background services unhealthy: {names}", data: data));
        }

        if (circuitStatus == HealthStatus.Unhealthy)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Background services heartbeat OK but {circuitMessage}", data: data));
        }

        if (circuitStatus == HealthStatus.Degraded)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Background services heartbeat OK but {circuitMessage}", data: data));
        }

        if (heartbeats.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "No background services registered yet", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"All {heartbeats.Count} background services healthy", data: data));
    }
}
