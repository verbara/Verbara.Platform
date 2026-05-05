using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;

namespace Verbara.Platform.Routing.Inbound.Tests;

public class RoundRobinAgentSelectorTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId QueueId = EntityId.From("q-1");
    private static readonly string[] AgentIds12 = ["agent-1", "agent-2"];

    private static Agent MakeAgent(string id) => new()
    {
        AgentId = EntityId.From(id),
        TenantId = Tenant,
        UserId = EntityId.New(),
        DisplayName = $"Agent {id}",
        State = AgentState.Available,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task SelectAgentAsync_ShouldReturnNull_WhenNoAgentsAvailable()
    {
        var presence = Substitute.For<IAgentPresenceService>();
        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Agent>());

        var selector = new RoundRobinAgentSelector(presence);

        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldReturnPreferredAgent_WhenPreferredIsAvailable()
    {
        var presence = Substitute.For<IAgentPresenceService>();
        var agent1 = MakeAgent("agent-1");
        var agent2 = MakeAgent("agent-2");
        var preferredId = agent2.AgentId;

        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns(new[] { agent1, agent2 });

        var selector = new RoundRobinAgentSelector(presence);

        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, preferredId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be("agent-2");
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldFallBackToRoundRobin_WhenPreferredIsNotAvailable()
    {
        var presence = Substitute.For<IAgentPresenceService>();
        var agent1 = MakeAgent("agent-1");
        var agent2 = MakeAgent("agent-2");
        var notAvailableId = EntityId.From("agent-999");

        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns(new[] { agent1, agent2 });

        var selector = new RoundRobinAgentSelector(presence);

        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, notAvailableId, CancellationToken.None);

        result.Should().NotBeNull();
        // Should have selected one of the available agents via round-robin
        AgentIds12.Should().Contain(result!.Value.Value);
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldRotateAgents_WhenCalledMultipleTimes()
    {
        var presence = Substitute.For<IAgentPresenceService>();
        var agent1 = MakeAgent("agent-1");
        var agent2 = MakeAgent("agent-2");
        var agent3 = MakeAgent("agent-3");

        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns(new[] { agent1, agent2, agent3 });

        var selector = new RoundRobinAgentSelector(presence);

        var results = new HashSet<string>();
        for (var i = 0; i < 9; i++)
        {
            var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, null, CancellationToken.None);
            results.Add(result!.Value.Value);
        }

        // Over 9 calls with 3 agents, all agents should be selected at least once
        results.Should().Contain("agent-1");
        results.Should().Contain("agent-2");
        results.Should().Contain("agent-3");
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldReturnSingleAgent_WhenOnlyOneAvailable()
    {
        var presence = Substitute.For<IAgentPresenceService>();
        var agent1 = MakeAgent("agent-solo");

        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns(new[] { agent1 });

        var selector = new RoundRobinAgentSelector(presence);

        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be("agent-solo");
    }
}
