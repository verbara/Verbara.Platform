namespace Verbara.Platform.Api.Health;

/// <summary>
/// Configuration for HealthCheck circuit-state awareness (Platform v1.9.1 / Frente D).
/// </summary>
public sealed class PlatformHealthCheckOptions
{
    /// <summary>
    /// Policy keys whose circuit state contributes to the <c>postgres</c> health check.
    /// </summary>
    public IList<string> PostgresWatchedPolicyKeys { get; } = new List<string>
    {
        "healthcheck.postgres",
    };

    /// <summary>
    /// Policy keys whose circuit state contributes to the <c>services</c> health check.
    /// </summary>
    public IList<string> WorkerWatchedPolicyKeys { get; } = new List<string>
    {
        "webhook.delivery",
        "smtp.send",
        "oidc.token-exchange",
        "worker.asterisk-capacity-sync",
    };

    /// <summary>
    /// Duration after which a circuit being Open escalates the health check from
    /// Degraded to Unhealthy. Default: 5 minutes.
    /// </summary>
    public TimeSpan UnhealthyOpenThreshold { get; set; } = TimeSpan.FromMinutes(5);
}
