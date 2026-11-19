using FluentAssertions;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// call-records-name-the-queue-agent (design D1; task 2.1): the event store's agent resolver names the Platform agent of
/// the record's own tenant and answers <see langword="null"/>, which keeps the Sdk's agent id, for any other interface.
/// </summary>
public sealed class AgentInterfaceCallAgentResolverTests
{
    private const string Tenant = "t1";
    private const string AgentGuid = "0123456789abcdef0123456789abcdef";

    private readonly AgentInterfaceCallAgentResolver _resolver = new();

    [Fact]
    public async Task ResolveAgentIdAsync_ShouldReturnTheAgentId_WhenTheInterfaceIsAnAgentOfTheRecordsTenant()
    {
        var answer = await _resolver.ResolveAgentIdAsync(Tenant, $"PJSIP/{Tenant}-agent-{AgentGuid}");

        answer.Should().Be(AgentGuid);
    }

    [Theory]
    [InlineData("PJSIP/t2-agent-0123456789abcdef0123456789abcdef")]
    [InlineData("PJSIP/t1-agent-sup-u1")]
    [InlineData("PJSIP/t1-agent-   ")]
    [InlineData("PJSIP/t1-agent-")]
    [InlineData("Local/1001@from-queue/n")]
    public async Task ResolveAgentIdAsync_ShouldReturnNull_WhenTheInterfaceNamesNoAgentOfTheRecordsTenant(
        string agentInterface)
    {
        var answer = await _resolver.ResolveAgentIdAsync(Tenant, agentInterface);

        answer.Should().BeNull("an interface of another tenant, a listen slot, a blank id or a Local member keeps the "
            + "Sdk's agent id");
    }
}
