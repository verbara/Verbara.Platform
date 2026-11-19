using Microsoft.Extensions.DependencyInjection;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.Tests.EventStore;

/// <summary>
/// call-records-name-the-queue-agent (design D3; task 1.2): the Api host built from <c>Program</c> against its pinned Pro
/// packages registers an <see cref="ICallAgentResolver"/>, so no call record is written by a host whose event store asks
/// for an agent resolver and finds none. The reference to the Pro type is the version threshold: below the Pro release
/// that added the seam the type does not exist and this file does not compile, so no package version literal is asserted
/// (living spec <c>sdk-pin-cascade</c>).
/// </summary>
public sealed class CallAgentResolverRegisteredTests : IClassFixture<PlatformApiFactory>
{
    private const string TenantId = "t1";
    private const string AgentId = "0123456789abcdef0123456789abcdef";

    private readonly PlatformApiFactory _factory;

    public CallAgentResolverRegisteredTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Host_ShouldResolveACallAgentResolverThatNamesThePlatformAgent_WhenBuiltFromProgram()
    {
        var resolver = _factory.Services.GetService<ICallAgentResolver>();

        resolver.Should().NotBeNull(
            "the Api host must register an ICallAgentResolver: without it every answered queue call's record keeps "
            + "agent_id null and Pro logs that no ICallAgentResolver is registered");
        var answer = await resolver!.ResolveAgentIdAsync(TenantId, $"PJSIP/{TenantId}-agent-{AgentId}");
        answer.Should().Be(AgentId, "the host's resolver maps Platform's member interface to its agent id");
    }
}
