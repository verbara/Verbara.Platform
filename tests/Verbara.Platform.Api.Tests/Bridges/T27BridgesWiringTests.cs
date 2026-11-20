using Verbara.Sdk.Pro.Push.SignalR.Bridges;
using Verbara.Sdk.Pro.Push.SignalR.Bridges.Diagnostics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Tests.Bridges;

/// <summary>
/// Verifies that Platform.Api's Program.cs wires the one remaining T27 event
/// bridge, ClusterEventPushBridge, via the Pro.Push.SignalR DI extensions with
/// the expected BridgeOptions, and wires no other Pro event bridge: hub agent and
/// conversation frames come from Platform's own Core events (Pro/ADR-0026 point 4;
/// owner Q8 = A, 2026-11-18). The bridge still lives in Platform.Api after
/// ADR-0022 Phase A — only the Hub host moved to Verbara.Platform.Realtime.
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
    public async Task Platform_ShouldNotRegisterProAgentOrConversationBridge_WhenHostIsBuilt()
    {
        await using var factory = new PlatformApiFactory();
        _ = factory.CreateClient();

        var hostedServices = factory.ProgramHostedServiceTypeNames;

        // Positive control: the capture ran before the test stubs and sees Pro's type-based bridge registrations.
        hostedServices.Should().Contain(
            "Verbara.Sdk.Pro.Push.SignalR.Bridges.ClusterEventPushBridge",
            "Program.cs still wires WithClusterEventBridge()");

        // String literals, not typeof: Pro deletes both types (Pro/ADR-0026 point 4; the conversation bridge after this
        // change merges).
        using (new AssertionScope())
        {
            hostedServices.Should().NotContain(
                "Verbara.Sdk.Pro.Push.SignalR.Bridges.AgentStatePushBridge",
                "agent state reaches the hub from Platform's own agent.state_changed event, not from the Pro bridge");
            hostedServices.Should().NotContain(
                "Verbara.Sdk.Pro.Push.SignalR.Bridges.ConversationStatePushBridge",
                "conversation state reaches the hub from Platform's own conversation.state_changed event, not from the Pro bridge");
        }
    }
}
