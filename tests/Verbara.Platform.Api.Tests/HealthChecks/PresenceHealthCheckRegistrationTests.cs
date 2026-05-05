using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Tests.HealthChecks;

/// <summary>
/// R5.3 Phase A Wave 2 Task A.5.b — verify the 4 Pro health checks added by
/// Pro task A.5 (commit 05adeb8) are wired into the Platform health-check
/// registry with the <c>"ready"</c> tag so they participate in
/// <c>/health/ready</c>.
/// </summary>
public sealed class PresenceHealthCheckRegistrationTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public PresenceHealthCheckRegistrationTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void PresenceHealthChecks_ShouldBeRegistered_WithReadyTag()
    {
        var options = _factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        var expected = new[]
        {
            "presence-heartbeat",
            "presence-fanout",
            "presence-merge",
            "retention",
        };

        foreach (var name in expected)
        {
            var registration = options.Registrations
                .FirstOrDefault(r => r.Name == name);

            registration.Should().NotBeNull(
                "Pro health check '{0}' must be registered (R5.3 A.5.b)", name);
            registration!.Tags.Should().Contain(
                "ready",
                "health check '{0}' must participate in /health/ready", name);
        }
    }
}
