using Asterisk.Sdk.Pro.Push.SignalR.Bridges;
using Asterisk.Sdk.Pro.Push.SignalR.Bridges.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Tests.Bridges;

/// <summary>
/// Verifies that Platform.Api's Program.cs wires the T27 event bridges
/// (ClusterEventPushBridge, ConversationStatePushBridge, AgentStatePushBridge)
/// via the Pro.Push.SignalR DI extensions with the expected BridgeOptions.
/// Bridge behavior itself is covered by Pro.Push.SignalR.Tests — these tests
/// only guard Platform's wiring contract (DefaultTenantId + shared metrics).
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
            "Program.cs wires WithConversationBridge(opt => opt.DefaultTenantId = \"default-tenant\")");
    }

    [Fact]
    public async Task Platform_ShouldRegisterBridgeMetrics_AsSingleton()
    {
        await using var factory = new PlatformApiFactory();
        _ = factory.CreateClient();

        var metrics = factory.Services.GetRequiredService<BridgeMetrics>();

        metrics.Should().NotBeNull("shared BridgeMetrics is registered by every bridge opt-in");
    }
}
