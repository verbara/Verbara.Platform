using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Health;
using Verbara.Sdk.Resilience;
using Verbara.Sdk.Resilience.Diagnostics;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// The <c>postgres</c> readiness probe runs on every evaluation (spec <c>api-readiness-scope</c>, "The postgres
/// readiness probe SHALL run on every evaluation"). The policy that bounds the probe, as
/// <see cref="PlatformHealthExtensions.AddPlatformHealthChecks"/> registers it, has a timeout and no circuit
/// breaker, so a sustained database outage is <c>Unhealthy</c> on every evaluation from the first failed probe,
/// never a circuit rejection, the key never reaches the circuit-state gauge, and the first successful probe is
/// <c>Healthy</c> again.
///
/// <para>The observer runs against the real static gauge. No other test executes a policy under
/// <c>healthcheck.postgres</c> with a circuit (<see cref="PostgresHealthCheckTests"/> builds policies without one),
/// so <c>unobserved</c> is stable.</para>
/// </summary>
public sealed class PostgresProbeHasNoCircuitTests
{
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutageLength = TimeSpan.FromMinutes(6);

    private static ResiliencePolicy RegisteredProbePolicy()
    {
        var services = new ServiceCollection();
        services.AddPlatformHealthChecks();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredKeyedService<ResiliencePolicy>(PostgresHealthCheck.ResiliencePolicyKey);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldRunTheProbeAndReportUnhealthyOnEveryEvaluation_WhenPostgresIsDownForSixMinutes()
    {
        var policy = RegisteredProbePolicy();
        var time = new FakeTimeProvider();
        using var observer = new ResilienceStateObserver(time, EvaluationInterval);
        await observer.StartAsync(CancellationToken.None);

        var probes = 0;
        var databaseUp = false;
        var check = new PostgresHealthCheck(
            probe: _ =>
            {
                probes++;
                return databaseUp ? Task.CompletedTask : throw new InvalidOperationException("connection refused");
            },
            observer,
            Options.Create(new PlatformHealthCheckOptions()),
            policy);

        var evaluations = 0;
        var elapsed = TimeSpan.Zero;
        while (elapsed < OutageLength)
        {
            time.Advance(EvaluationInterval);
            elapsed += EvaluationInterval;

            var result = await check.CheckHealthAsync(new HealthCheckContext());
            evaluations++;

            result.Status.Should().Be(HealthStatus.Unhealthy, "evaluation {0} of a down database", evaluations);
            result.Description.Should().Be("PostgreSQL connection failed",
                "evaluation {0} reports the probe's own failure, never a circuit rejection", evaluations);
            result.Exception.Should().BeOfType<InvalidOperationException>("evaluation {0}", evaluations);
            result.Data[PostgresHealthCheck.ResiliencePolicyKey].Should().Be("unobserved",
                "the probe's key has no circuit, so the gauge never reports it (evaluation {0})", evaluations);
        }

        evaluations.Should().Be(72, "six minutes at one evaluation every five seconds");
        probes.Should().Be(evaluations, "the probe ran once per evaluation; nothing short-circuited it");
        policy.GetCircuitState(PostgresHealthCheck.ResiliencePolicyKey)
            .Should().Be(ResilienceMetrics.CircuitStateValue.Closed, "a policy without a breaker never opens");

        databaseUp = true;
        time.Advance(EvaluationInterval);
        var recovered = await check.CheckHealthAsync(new HealthCheckContext());

        recovered.Status.Should().Be(HealthStatus.Healthy, "the first successful probe is Healthy again");
        probes.Should().Be(evaluations + 1);

        await observer.StopAsync(CancellationToken.None);
    }
}
