using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;
using NSubstitute;
using Xunit;
using FluentAssertions;

namespace Verbara.Platform.Queues.Tests.Services;

public class AgentPresenceServiceTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId AgentId = EntityId.From("a-001");
    private static readonly EntityId QueueId = EntityId.From("q-001");

    private static Agent MakeAgent(
        AgentState state = AgentState.Available,
        IReadOnlyList<string>? skills = null) => new()
    {
        AgentId = AgentId,
        TenantId = Tenant,
        UserId = EntityId.From("u-001"),
        DisplayName = "Agent",
        State = state,
        Skills = skills ?? [],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Queue MakeQueue(IReadOnlyList<string>? requiredSkills = null) => new()
    {
        QueueId = QueueId,
        TenantId = Tenant,
        Name = "Support",
        RequiredSkills = requiredSkills ?? [],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static PagedResult<Agent> PageOf(params Agent[] agents) =>
        new(agents, agents.Length, 1, int.MaxValue);

    private static (InMemoryAgentPresenceService Sut, IAgentStore AgentStore, IQueueStore QueueStore, IAgentCapacityService Capacity) CreateSut(
        Agent? agent = null,
        Queue? queue = null,
        bool hasCapacity = true)
    {
        var agentStore = Substitute.For<IAgentStore>();
        var queueStore = Substitute.For<IQueueStore>();
        var capacity = Substitute.For<IAgentCapacityService>();

        var resolvedAgent = agent ?? MakeAgent();
        agentStore.GetByIdAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
                  .Returns(resolvedAgent);
        agentStore.SaveAsync(Arg.Any<Agent>(), Arg.Any<CancellationToken>())
                  .Returns(Task.CompletedTask);
        agentStore.ListAsync(Tenant, Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
                  .Returns(PageOf(resolvedAgent));

        queueStore.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
                  .Returns(queue ?? MakeQueue());

        capacity.HasCapacityAsync(Tenant, AgentId, Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
                .Returns(hasCapacity);

        var sut = new InMemoryAgentPresenceService(agentStore, queueStore, capacity);
        return (sut, agentStore, queueStore, capacity);
    }

    [Fact]
    public async Task UpdateState_ShouldTransition_WhenValid()
    {
        var (sut, _, _, _) = CreateSut(MakeAgent(AgentState.Available));

        await sut.UpdateStateAsync(Tenant, AgentId, AgentState.Busy, CancellationToken.None);

        var state = await sut.GetStateAsync(Tenant, AgentId, CancellationToken.None);
        state.Should().Be(AgentState.Busy);
    }

    [Fact]
    public async Task UpdateState_ShouldThrow_WhenInvalidTransition()
    {
        var (sut, _, _, _) = CreateSut(MakeAgent(AgentState.Offline));

        var act = () => sut.UpdateStateAsync(Tenant, AgentId, AgentState.Busy, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Offline*Busy*");
    }

    [Fact]
    public async Task GetState_ShouldReturnCurrentState()
    {
        var (sut, _, _, _) = CreateSut(MakeAgent(AgentState.Break));

        var state = await sut.GetStateAsync(Tenant, AgentId, CancellationToken.None);

        state.Should().Be(AgentState.Break);
    }

    [Fact]
    public async Task GetAvailableAgents_ShouldFilterBySkills()
    {
        var agentWithSkill = MakeAgent(skills: ["billing"]);
        var agentStore = Substitute.For<IAgentStore>();
        var queueStore = Substitute.For<IQueueStore>();
        var capacity = Substitute.For<IAgentCapacityService>();

        var agentWithoutSkill = new Agent
        {
            AgentId = EntityId.From("a-002"),
            TenantId = Tenant,
            UserId = EntityId.From("u-002"),
            DisplayName = "Agent B",
            State = AgentState.Available,
            Skills = ["sales"],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        agentStore.GetByIdAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
                  .Returns(agentWithSkill);
        agentStore.ListAsync(Tenant, Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
                  .Returns(new PagedResult<Agent>([agentWithSkill, agentWithoutSkill], 2, 1, int.MaxValue));
        agentStore.SaveAsync(Arg.Any<Agent>(), Arg.Any<CancellationToken>())
                  .Returns(Task.CompletedTask);

        queueStore.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
                  .Returns(MakeQueue(requiredSkills: ["billing"]));

        capacity.HasCapacityAsync(Tenant, Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
                .Returns(true);

        var sut = new InMemoryAgentPresenceService(agentStore, queueStore, capacity);

        var result = await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().ContainSingle(a => a.AgentId == AgentId);
        result.Should().NotContain(a => a.AgentId == agentWithoutSkill.AgentId);
    }

    [Fact]
    public async Task GetAvailableAgents_ShouldFilterByRoutableState()
    {
        var (sut, _, _, _) = CreateSut(MakeAgent(AgentState.Break));

        var result = await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAvailableAgents_ShouldFilterByCapacity()
    {
        var (sut, _, _, _) = CreateSut(MakeAgent(AgentState.Available), hasCapacity: false);

        var result = await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAvailableAgentsAsync_ShouldExcludeAgent_WhenPendingStateSet()
    {
        var agentWithPending = MakeAgent(AgentState.Available);
        agentWithPending.PendingState = AgentState.Break;
        var (sut, _, _, _) = CreateSut(agentWithPending);

        var result = await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAvailableAgentsAsync_ShouldIncludeAgent_WhenNoPending()
    {
        var (sut, _, _, _) = CreateSut(MakeAgent(AgentState.Available));

        var result = await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().ContainSingle(a => a.AgentId == AgentId);
    }

    [Fact]
    public async Task GetAvailableAgents_ShouldReturnEmpty_WhenNoMatch()
    {
        var agentStore = Substitute.For<IAgentStore>();
        var queueStore = Substitute.For<IQueueStore>();
        var capacity = Substitute.For<IAgentCapacityService>();

        agentStore.ListAsync(Tenant, Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
                  .Returns(PagedResult<Agent>.Empty(1, int.MaxValue));
        queueStore.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
                  .Returns(MakeQueue());

        var sut = new InMemoryAgentPresenceService(agentStore, queueStore, capacity);

        var result = await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeEmpty();
    }
}
