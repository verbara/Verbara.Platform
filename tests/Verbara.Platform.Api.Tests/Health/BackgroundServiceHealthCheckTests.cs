using Verbara.Platform.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Tests.Health;

public class BackgroundServiceHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthy_WhenAllHeartbeatsOkAndNoCircuits()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        heartbeat.RecordTick("Worker2", TimeSpan.FromSeconds(10));
        var observer = new ResilienceStateObserver();
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new BackgroundServiceHealthCheck(heartbeat, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenHeartbeatStale()
    {
        var heartbeat = new ServiceHeartbeat();
        // Force stale: 1ms expected, last tick ~2s ago relative to test execution.
        // We use reflection-free test by recording with a tiny interval and then waiting.
        // Simpler: manipulate via two ticks where the second doesn't happen.
        heartbeat.RecordTick("Worker1", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        var observer = new ResilienceStateObserver();
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new BackgroundServiceHealthCheck(heartbeat, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Worker1");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnDegraded_WhenWorkerCircuitOpenLessThan5Min()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        var observer = new ResilienceStateObserver();
        observer.SetForTest(
            "webhook.delivery",
            ResilienceCircuitState.Open,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new BackgroundServiceHealthCheck(heartbeat, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("webhook.delivery");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenWorkerCircuitOpenMoreThan5Min()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        var observer = new ResilienceStateObserver();
        observer.SetForTest(
            "smtp.send",
            ResilienceCircuitState.Open,
            DateTimeOffset.UtcNow.AddMinutes(-10));
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new BackgroundServiceHealthCheck(heartbeat, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("smtp.send");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldPreferHeartbeatUnhealthy_OverCircuitDegraded()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("StaleWorker", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        var observer = new ResilienceStateObserver();
        observer.SetForTest(
            "webhook.delivery",
            ResilienceCircuitState.Open,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new BackgroundServiceHealthCheck(heartbeat, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // Heartbeat-driven Unhealthy dominates over circuit-driven Degraded.
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("StaleWorker");
    }
}
