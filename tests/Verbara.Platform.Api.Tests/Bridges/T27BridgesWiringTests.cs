using Verbara.Sdk.Pro.Push.SignalR.Bridges;
using Verbara.Sdk.Pro.Push.SignalR.Bridges.Diagnostics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Tests.Bridges;

/// <summary>
/// Verifies that Platform.Api's Program.cs wires no Pro push bridge: hub agent and
/// conversation frames come from Platform's own Core events (Pro/ADR-0026 point 4;
/// owner Q8 = A, 2026-11-18), and the cluster bridge is gone too
/// (platform-drops-the-cluster-bridge): no hub client ever received its frames, and
/// /admin/cluster reads cluster state over REST.
/// </summary>
public sealed class T27BridgesWiringTests
{
    [Fact]
    public async Task Platform_ShouldConfigureBridgeOptions_WithPlatformDefaultTenant()
    {
        await using var factory = new PlatformApiFactory();
        _ = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<BridgeOptions>>();

        options.Value.DefaultTenantId.Should().Be(
            "default-tenant",
            "Program.cs wires WithDefaultTenantId(\"default-tenant\"), which the cluster bridge's loop needs for every event");
    }

    [Fact]
    public async Task Platform_ShouldRegisterBridgeMetrics_AsSingleton()
    {
        await using var factory = new PlatformApiFactory();
        _ = factory.CreateClient();

        var metrics = factory.Services.GetRequiredService<BridgeMetrics>();

        metrics.Should().NotBeNull("shared BridgeMetrics is registered by every bridge opt-in");
    }

    [Fact]
    public async Task Platform_ShouldNotRegisterAnyProPushBridge_WhenHostIsBuilt()
    {
        await using var factory = new PlatformApiFactory();
        _ = factory.CreateClient();

        var hostedServices = factory.ProgramHostedServiceTypeNames;

        // Positive control: Program.cs registers this listener unconditionally and by type, and the test stub strips
        // every Verbara hosted service, so only a capture taken before the stub sees it.
        hostedServices.Should().Contain(
            "Verbara.Platform.Core.Push.UserAccessRevocationListener",
            "Program.cs registers it unconditionally; a capture without it ran after the stub and proves nothing");

        // A prefix as a string, not typeof: it covers every bridge of the package and compiles after Pro deletes them.
        hostedServices.Where(name => name.StartsWith("Verbara.Sdk.Pro.Push.SignalR.", StringComparison.Ordinal))
            .Should().BeEmpty(
                "hub frames come from Platform's own Core events and the Api wires no Pro push bridge (Pro/ADR-0026)");
    }
}
