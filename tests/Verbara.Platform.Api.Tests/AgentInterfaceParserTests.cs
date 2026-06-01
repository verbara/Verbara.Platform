using Verbara.Platform.Api.Services;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Locks the shared <see cref="AgentInterfaceParser"/> contract — it derives the platform agentId
/// that BOTH the voice screen-pop and the agent-assist events carry, and that the React client
/// filters on. A wrong/blank parse silently drops every agent-targeted event (the P1 trap).
/// </summary>
public sealed class AgentInterfaceParserTests
{
    private const string Tenant = "acme";
    private const string AgentGuid = "11111111111111111111111111111111";

    [Fact]
    public void ExtractAgentId_ShouldReturnAgentId_WhenInterfaceMatchesTenant()
    {
        var result = AgentInterfaceParser.ExtractAgentId(Tenant, $"PJSIP/{Tenant}-agent-{AgentGuid}");

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be(AgentGuid);
    }

    [Fact]
    public void ExtractAgentId_ShouldReturnNull_WhenInterfaceNull()
    {
        AgentInterfaceParser.ExtractAgentId(Tenant, null).Should().BeNull();
    }

    [Fact]
    public void ExtractAgentId_ShouldReturnNull_WhenNotAnAgentEndpoint()
    {
        // A trunk endpoint, not an agent — must not be mis-parsed as an agent.
        AgentInterfaceParser.ExtractAgentId(Tenant, "PJSIP/t-2-00000001").Should().BeNull();
    }

    [Fact]
    public void ExtractAgentId_ShouldReturnNull_WhenSuffixWhitespace()
    {
        // EntityId.From throws on a whitespace-only suffix — must be guarded to null, not throw.
        AgentInterfaceParser.ExtractAgentId(Tenant, $"PJSIP/{Tenant}-agent-   ").Should().BeNull();
    }

    [Fact]
    public void ExtractAgentId_ShouldReturnNull_WhenWrongTenantPrefix()
    {
        AgentInterfaceParser.ExtractAgentId(Tenant, $"PJSIP/other-agent-{AgentGuid}").Should().BeNull();
    }
}
