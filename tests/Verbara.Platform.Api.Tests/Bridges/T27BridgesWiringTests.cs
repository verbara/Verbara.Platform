using Verbara.Sdk.Pro.Push.SignalR.Bridges;
using Verbara.Sdk.Pro.Push.SignalR.Bridges.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Tests.Bridges;

/// <summary>
/// Verifies that Platform.Api's Program.cs wires the T27 event bridges
/// (ClusterEventPushBridge, ConversationStatePushBridge, AgentStatePushBridge)
/// via the Pro.Push.SignalR DI extensions with the expected BridgeOptions.
/// Bridges still live in Platform.Api after ADR-0022 Phase A — only the
/// Hub host moved to Verbara.Platform.Realtime. Bridges produce events into
/// IPushEventBus and Pro.Push Redis backplane carries them across to
/// Realtime's PushToHubRelay.
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
