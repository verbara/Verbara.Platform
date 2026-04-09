using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Platform.Api.Health;

internal sealed class BackgroundServiceHealthCheck : IHealthCheck
{
    private readonly IServiceHeartbeat _heartbeat;

    public BackgroundServiceHealthCheck(IServiceHeartbeat heartbeat)
    {
        _heartbeat = heartbeat;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var all = _heartbeat.GetAll();
        if (all.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("No background services registered yet"));

        var unhealthy = all.Where(kv => !kv.Value.IsHealthy).ToList();
        if (unhealthy.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy($"All {all.Count} background services healthy"));

        var names = string.Join(", ", unhealthy.Select(kv => kv.Key));
        return Task.FromResult(HealthCheckResult.Unhealthy($"Background services unhealthy: {names}"));
    }
}
