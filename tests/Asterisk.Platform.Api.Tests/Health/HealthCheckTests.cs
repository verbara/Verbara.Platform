using Asterisk.Platform.Api.Health;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Tests.Health;

public class HealthCheckTests
{
    private static BackgroundServiceHealthCheck BuildCheck(ServiceHeartbeat heartbeat)
    {
        var observer = new ResilienceStateObserver();
        var options = Options.Create(new PlatformHealthCheckOptions());
        return new BackgroundServiceHealthCheck(heartbeat, observer, options);
    }

    // ── ServiceHeartbeat ─────────────────────────────────────────────────────────

    [Fact]
    public void IsHealthy_ShouldReturnTrue_WhenServiceJustTicked()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("TestService", TimeSpan.FromSeconds(60));

        heartbeat.IsHealthy("TestService").Should().BeTrue();
    }

    [Fact]
    public void IsHealthy_ShouldReturnTrue_WhenServiceNeverRegistered()
    {
        var heartbeat = new ServiceHeartbeat();

        // Unknown services are considered healthy (haven't started yet)
        heartbeat.IsHealthy("UnknownService").Should().BeTrue();
    }

    [Fact]
    public void GetAll_ShouldReturnAllRegisteredServices()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("ServiceA", TimeSpan.FromSeconds(30));
        heartbeat.RecordTick("ServiceB", TimeSpan.FromSeconds(60));

        var all = heartbeat.GetAll();

        all.Should().HaveCount(2);
        all.Should().ContainKey("ServiceA");
        all.Should().ContainKey("ServiceB");
        all["ServiceA"].IsHealthy.Should().BeTrue();
        all["ServiceB"].IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void IsHealthy_ShouldReturnTrue_WhenMultipleTicks()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker", TimeSpan.FromSeconds(5));
        heartbeat.RecordTick("Worker", TimeSpan.FromSeconds(5));
        heartbeat.RecordTick("Worker", TimeSpan.FromSeconds(5));

        heartbeat.IsHealthy("Worker").Should().BeTrue();
    }

    // ── BackgroundServiceHealthCheck ─────────────────────────────────────────────

    [Fact]
    public async Task BackgroundServiceHealthCheck_ShouldReturnHealthy_WhenNoServicesRegistered()
    {
        var heartbeat = new ServiceHeartbeat();
        var check = BuildCheck(heartbeat);

        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

        result.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
    }

    [Fact]
    public async Task BackgroundServiceHealthCheck_ShouldReturnHealthy_WhenAllServicesHealthy()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        heartbeat.RecordTick("Worker2", TimeSpan.FromSeconds(10));
        var check = BuildCheck(heartbeat);

        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

        result.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
    }
}
