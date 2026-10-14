using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// The <c>services</c> readiness check: can this pod's digital background workers keep up (readiness-without-asterisk,
/// design D3; Platform/ADR-0025 addendum 2026-10-13).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A digital worker heartbeat stale past three intervals (<see cref="ServiceHeartbeat"/>) makes it Unhealthy,
/// naming the worker, and dominates any circuit.</item>
/// <item>The voice-plane heartbeats (<see cref="VoicePlaneHeartbeats"/>) are left out of its status and its data; the
/// <c>asterisk</c> check on <c>/health/voice</c> reports them.</item>
/// <item>An open dependency circuit (<see cref="PlatformHealthCheckOptions.WorkerWatchedPolicyKeys"/>) makes it
/// Degraded at most, whatever its duration, because draining the pod heals no outbound integration, the keys are
/// global to every tenant, and the Sdk gauge reads Open until a call succeeds. Each key's state stays in the data.</item>
/// </list>
/// </remarks>
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
        var heartbeats = _heartbeat.GetAll()
            .Where(kv => !VoicePlaneHeartbeats.Names.Contains(kv.Key))
            .ToList();
        var unhealthyHeartbeats = heartbeats.Where(kv => !kv.Value.IsHealthy).ToList();

        // No escalation threshold: a dependency circuit is Degraded at most (design D3).
        var (circuitStatus, circuitMessage, circuitData) = CircuitHealthEvaluator.EvaluateCircuits(
            _healthOptions.WorkerWatchedPolicyKeys,
            unhealthyThreshold: null,
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

        // Heartbeat dominates — a stalled digital worker is always Unhealthy regardless of circuits.
        if (unhealthyHeartbeats.Count > 0)
        {
            var names = string.Join(", ", unhealthyHeartbeats.Select(kv => kv.Key));
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Background services unhealthy: {names}", data: data));
        }

        // Any open circuit is capped at Degraded here as well, so the check never gates readiness on one.
        if (circuitStatus != HealthStatus.Healthy)
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
