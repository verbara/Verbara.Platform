namespace Verbara.Platform.Api.Health;

/// <summary>
/// Configuration for HealthCheck circuit-state awareness (Platform v1.9.1 / Frente D).
/// </summary>
public sealed class PlatformHealthCheckOptions
{
    /// <summary>
    /// Policy keys whose circuit state contributes to the <c>postgres</c> health check. The default key,
    /// <c>healthcheck.postgres</c>, names the probe's own policy, which has no circuit breaker, so it never reaches the
    /// gauge and reads <c>unobserved</c>: the probe runs on every evaluation and a failed probe is Unhealthy by itself.
    /// </summary>
    public IList<string> PostgresWatchedPolicyKeys { get; } = new List<string>
    {
        "healthcheck.postgres",
    };

    /// <summary>
    /// Policy keys whose circuit state the <c>services</c> health check reports: the dependency circuits the Api
    /// executes for outbound integrations. An open one makes <c>services</c> <c>Degraded</c> at most, whatever its
    /// duration (readiness-without-asterisk, design D3; Platform/ADR-0025 addendum 2026-10-13).
    /// </summary>
    /// <remarks>
    /// <c>smtp.send</c> is not watched: only <c>SmtpSender</c> in the Mail service executes it, so this process never
    /// observes it. Nor is <c>worker.asterisk-capacity-sync</c>: it has no caller, and it belongs to the voice plane.
    /// </remarks>
    public IList<string> WorkerWatchedPolicyKeys { get; } = new List<string>
    {
        "webhook.delivery",
        "oidc.token-exchange",
    };

    /// <summary>
    /// Duration after which a watched circuit read <c>Open</c> without a break escalates the <c>postgres</c> health
    /// check from Degraded to Unhealthy. Default: 5 minutes. A circuit that cycles through <c>HalfOpen</c> never gives
    /// such a reading, so it stays Degraded. The <c>services</c> check never escalates (design D3).
    /// </summary>
    public TimeSpan UnhealthyOpenThreshold { get; set; } = TimeSpan.FromMinutes(5);
}
