using Verbara.Sdk.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Health;

internal sealed class AsteriskAmiHealthCheck : IHealthCheck
{
    private readonly VerbaraOptions? _options;
    private readonly IResilienceStateObserver _observer;
    private readonly PlatformHealthCheckOptions _healthOptions;

    public AsteriskAmiHealthCheck(
        IServiceProvider services,
        IResilienceStateObserver observer,
        IOptions<PlatformHealthCheckOptions> healthOptions)
    {
        _options = services.GetService<IOptions<VerbaraOptions>>()?.Value;
        _observer = observer;
        _healthOptions = healthOptions.Value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        // If AMI is not configured, report healthy (digital-only deployment)
        if (_options is null || string.IsNullOrWhiteSpace(_options.Ami?.Hostname))
            return Task.FromResult(HealthCheckResult.Healthy("Asterisk AMI not configured (digital-only mode)"));

        // AMI connectivity is checked by the SDK AmiClient; Platform does not own a probe
        // here. The base signal (configured but unprobeable) remains Degraded, and we
        // overlay circuit-state awareness on top so a stuck AMI worker surfaces clearly.
        var baseMessage =
            $"AMI configured at {_options.Ami.Hostname}:{_options.Ami.Port} — connectivity depends on SDK AmiClient";

        var (status, circuitMessage, data) = EvaluateCircuits(
            _healthOptions.AmiWatchedPolicyKeys,
            _healthOptions.UnhealthyOpenThreshold,
            _observer);

        if (status == HealthStatus.Unhealthy)
            return Task.FromResult(HealthCheckResult.Unhealthy($"{baseMessage}. {circuitMessage}", data: data));

        if (status == HealthStatus.Degraded)
            return Task.FromResult(HealthCheckResult.Degraded($"{baseMessage}. {circuitMessage}", data: data));

        // No circuits open for AMI-adjacent work — keep the historical Degraded signal
        // when AMI is configured (Platform can't confirm liveness from here) but hand
        // back an empty circuit-breakdown so /health/ready JSON remains consistent.
        return Task.FromResult(HealthCheckResult.Degraded(baseMessage, data: data));
    }

    internal static (HealthStatus Status, string Message, IReadOnlyDictionary<string, object> Data)
        EvaluateCircuits(
            IList<string> watchedKeys,
            TimeSpan unhealthyThreshold,
            IResilienceStateObserver observer)
    {
        var openRecent = new List<string>();
        var openLong = new List<string>();
        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var key in watchedKeys)
        {
            var snap = observer.GetSnapshot(key);
            if (snap is null)
            {
                data[key] = "unobserved";
                continue;
            }

            data[key] = new
            {
                state = snap.State.ToString(),
                stateSince = snap.StateSince,
                durationSeconds = Math.Round(snap.Duration.TotalSeconds, 1),
            };

            if (!snap.IsOpen)
                continue;

            if (snap.Duration >= unhealthyThreshold)
                openLong.Add(key);
            else
                openRecent.Add(key);
        }

        if (openLong.Count > 0)
        {
            return (
                HealthStatus.Unhealthy,
                $"Circuit open >{unhealthyThreshold.TotalMinutes:0}min: {string.Join(", ", openLong)}",
                data);
        }

        if (openRecent.Count > 0)
        {
            return (
                HealthStatus.Degraded,
                $"Circuit open: {string.Join(", ", openRecent)}",
                data);
        }

        return (HealthStatus.Healthy, "All watched circuits closed", data);
    }
}
