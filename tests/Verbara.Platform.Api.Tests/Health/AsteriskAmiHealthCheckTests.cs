using Verbara.Platform.Api.Health;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Tests.Health;

public class AsteriskAmiHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthy_WhenAmiNotConfigured()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var observer = new ResilienceStateObserver();
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new AsteriskAmiHealthCheck(services, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnDegraded_WhenAmiConfiguredAndNoCircuitsOpen()
    {
        var services = BuildServicesWithAmi();
        var observer = new ResilienceStateObserver();
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new AsteriskAmiHealthCheck(services, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("AMI configured");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnDegraded_WhenCircuitOpenLessThan5Min()
    {
        var services = BuildServicesWithAmi();
        var observer = new ResilienceStateObserver();
        observer.SetForTest(
            "worker.asterisk-capacity-sync",
            ResilienceCircuitState.Open,
            DateTimeOffset.UtcNow.AddMinutes(-2));
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new AsteriskAmiHealthCheck(services, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("Circuit open");
        result.Description.Should().Contain("worker.asterisk-capacity-sync");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenCircuitOpenMoreThan5Min()
    {
        var services = BuildServicesWithAmi();
        var observer = new ResilienceStateObserver();
        observer.SetForTest(
            "worker.asterisk-capacity-sync",
            ResilienceCircuitState.Open,
            DateTimeOffset.UtcNow.AddMinutes(-6));
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new AsteriskAmiHealthCheck(services, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Circuit open >5min");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnDegraded_WhenClosedCircuitsDoNotContribute()
    {
        var services = BuildServicesWithAmi();
        var observer = new ResilienceStateObserver();
        observer.SetForTest("asterisk.ami", ResilienceCircuitState.Closed, DateTimeOffset.UtcNow);
        var options = Options.Create(new PlatformHealthCheckOptions());

        var check = new AsteriskAmiHealthCheck(services, observer, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("AMI configured");
        result.Description.Should().NotContain("Circuit open");
    }

    private static ServiceProvider BuildServicesWithAmi()
    {
        var services = new ServiceCollection();
        services.Configure<VerbaraOptions>(o =>
        {
            o.Ami = new AmiConnectionOptions
            {
                Hostname = "test-ami",
                Port = 5038,
                Username = "admin",
                Password = "secret",
            };
        });
        return services.BuildServiceProvider();
    }
}
