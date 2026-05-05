using Verbara.Sdk.Resilience;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Verbara.Platform.Api.Health;

internal sealed class PostgresHealthCheck : IHealthCheck
{
    /// <summary>
    /// Keyed <see cref="ResiliencePolicy"/> used to bound the <c>SELECT 1</c>
    /// probe so Postgres-under-load conditions surface as Unhealthy with a clear
    /// reason instead of hanging on the outer HealthCheck timeout.
    /// </summary>
    public const string ResiliencePolicyKey = "healthcheck.postgres";

    private readonly Func<CancellationToken, Task> _probe;
    private readonly ResiliencePolicy? _policy;
    private readonly IResilienceStateObserver _observer;
    private readonly PlatformHealthCheckOptions _healthOptions;

    public PostgresHealthCheck(
        NpgsqlDataSource dataSource,
        IResilienceStateObserver observer,
        IOptions<PlatformHealthCheckOptions> healthOptions,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
        : this(ct => ProbeDataSourceAsync(dataSource, ct), observer, healthOptions, policy)
    {
    }

    /// <summary>Test-only seam: inject a custom probe delegate.</summary>
    internal PostgresHealthCheck(
        Func<CancellationToken, Task> probe,
        IResilienceStateObserver observer,
        IOptions<PlatformHealthCheckOptions> healthOptions,
        ResiliencePolicy? policy)
    {
        _probe = probe;
        _observer = observer;
        _healthOptions = healthOptions.Value;
        _policy = policy;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        // Consult watched circuits first — a long-open Postgres circuit is authoritative
        // even if a probe would succeed; it surfaces operator-visible backpressure that
        // the probe alone wouldn't catch.
        var (circuitStatus, circuitMessage, circuitData) = AsteriskAmiHealthCheck.EvaluateCircuits(
            _healthOptions.PostgresWatchedPolicyKeys,
            _healthOptions.UnhealthyOpenThreshold,
            _observer);

        try
        {
            if (_policy is not null)
            {
                await _policy.ExecuteAsync<int>(
                    ResiliencePolicyKey,
                    async token => { await _probe(token); return 0; },
                    ct);
            }
            else
            {
                await _probe(ct);
            }
        }
        catch (TimeoutException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"PostgreSQL probe exceeded {ResiliencePolicyKey} timeout",
                ex,
                data: circuitData);
        }
        catch (CircuitBreakerOpenException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"PostgreSQL probe circuit open ({ex.Key})",
                ex,
                data: circuitData);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection failed", ex, data: circuitData);
        }

        // Probe succeeded — report circuit-state-derived status.
        return circuitStatus switch
        {
            HealthStatus.Unhealthy => HealthCheckResult.Unhealthy(
                $"PostgreSQL probe OK but {circuitMessage}", data: circuitData),
            HealthStatus.Degraded => HealthCheckResult.Degraded(
                $"PostgreSQL probe OK but {circuitMessage}", data: circuitData),
            _ => HealthCheckResult.Healthy("PostgreSQL connection OK", data: circuitData),
        };
    }

    private static async Task ProbeDataSourceAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
    }
}
