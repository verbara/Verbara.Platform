using Verbara.Platform.Api.Services;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Locks <see cref="AgentAssistBridge.DeriveAgentId"/> — the agentId that agent-assist events carry
/// and the React client filters on (<c>isForCurrentAgent</c>). The bridge prefers the id parsed from
/// the realtime member interface because <c>CallSession.AgentId</c> is often empty for app_queue; a
/// blank tenant must NOT collapse the id (review finding #13, the client-drop trap).
/// </summary>
public sealed class AgentAssistBridgeTests
{
    private const string Tenant = "acme";
    private const string AgentGuid = "11111111111111111111111111111111";

    [Fact]
    public void DeriveAgentId_ShouldParseFromInterface_WhenTenantAndInterfacePresent()
    {
        var result = AgentAssistBridge.DeriveAgentId(Tenant, $"PJSIP/{Tenant}-agent-{AgentGuid}", fallbackAgentId: "");
        result.Should().Be(AgentGuid);
    }

    [Fact]
    public void DeriveAgentId_ShouldPreferInterface_OverFallback()
    {
        var result = AgentAssistBridge.DeriveAgentId(Tenant, $"PJSIP/{Tenant}-agent-{AgentGuid}", fallbackAgentId: "stale");
        result.Should().Be(AgentGuid);
    }

    [Fact]
    public void DeriveAgentId_ShouldFallBack_WhenInterfaceNullOrNonMatching()
    {
        AgentAssistBridge.DeriveAgentId(Tenant, agentInterface: null, fallbackAgentId: "fb").Should().Be("fb");
        AgentAssistBridge.DeriveAgentId(Tenant, "PJSIP/t-2-0001", fallbackAgentId: "fb").Should().Be("fb");
    }

    [Fact]
    public void DeriveAgentId_ShouldFallBack_WhenTenantEmpty()
    {
        // The P1-trap guard: an empty tenant must not be parsed (blank prefix) — use the fallback.
        AgentAssistBridge.DeriveAgentId("", $"PJSIP/{Tenant}-agent-{AgentGuid}", fallbackAgentId: "fb").Should().Be("fb");
    }

    [Fact]
    public void DeriveAgentId_ShouldReturnEmpty_WhenNothingResolves()
    {
        AgentAssistBridge.DeriveAgentId("", null, fallbackAgentId: null).Should().BeEmpty();
    }
}
