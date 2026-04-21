using Asterisk.Platform.Api.Health;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Tests.Health;

public class PostgresHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthy_WhenProbeSucceedsAndNoCircuitsOpen()
    {
        var observer = new ResilienceStateObserver();
        var options = Options.Create(new PlatformHealthCheckOptions());
        var policy = BuildShortTimeoutPolicy();

        var check = new PostgresHealthCheck(
            probe: _ => Task.CompletedTask,
            observer, options, policy);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("PostgreSQL connection OK");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenProbeExceedsTimeout()
    {
        var observer = new ResilienceStateObserver();
        var options = Options.Create(new PlatformHealthCheckOptions());
        // Very tight timeout (10ms) + a deliberately slow probe (1s) → TimeoutException.
        var policy = new ResiliencePolicyBuilder()
            .WithTimeout(TimeSpan.FromMilliseconds(10))
            .Build();

        var check = new PostgresHealthCheck(
            probe: async innerCt =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), innerCt); }
                catch (OperationCanceledException) { throw; }
            },
            observer, options, policy);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("timeout");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenProbeThrows()
    {
        var observer = new ResilienceStateObserver();
        var options = Options.Create(new PlatformHealthCheckOptions());
        var policy = BuildShortTimeoutPolicy();

        var check = new PostgresHealthCheck(
            probe: _ => throw new InvalidOperationException("connection refused"),
            observer, options, policy);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("PostgreSQL connection failed");
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnDegraded_WhenWatchedCircuitOpenLessThan5Min()
    {
        var observer = new ResilienceStateObserver();
        observer.SetForTest(
            "healthcheck.postgres",
            ResilienceCircuitState.Open,
            DateTimeOffset.UtcNow.AddMinutes(-2));
        var options = Options.Create(new PlatformHealthCheckOptions());
        var policy = BuildShortTimeoutPolicy();

        var check = new PostgresHealthCheck(
            probe: _ => Task.CompletedTask,
            observer, options, policy);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("healthcheck.postgres");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenWatchedCircuitOpenMoreThan5Min()
    {
        var observer = new ResilienceStateObserver();
        observer.SetForTest(
            "healthcheck.postgres",
            ResilienceCircuitState.Open,
            DateTimeOffset.UtcNow.AddMinutes(-10));
        var options = Options.Create(new PlatformHealthCheckOptions());
        var policy = BuildShortTimeoutPolicy();

        var check = new PostgresHealthCheck(
            probe: _ => Task.CompletedTask,
            observer, options, policy);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("PostgreSQL probe OK but Circuit open >5min");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthy_WhenNoPolicyProvided()
    {
        var observer = new ResilienceStateObserver();
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new PostgresHealthCheck(
            probe: _ => Task.CompletedTask,
            observer, options, policy: null);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    private static ResiliencePolicy BuildShortTimeoutPolicy()
        => new ResiliencePolicyBuilder()
            .WithTimeout(TimeSpan.FromSeconds(5))
            .Build();
}
