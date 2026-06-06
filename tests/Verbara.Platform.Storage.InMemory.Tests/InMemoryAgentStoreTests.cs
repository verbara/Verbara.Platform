using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryAgentStoreTests
{
    private static Agent MakeAgent(TenantId tenantId, AgentState state) =>
        new()
        {
            AgentId = EntityId.New(),
            TenantId = tenantId,
            UserId = EntityId.New(),
            DisplayName = "Agent",
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static async Task<List<Agent>> CollectAsync(IAsyncEnumerable<Agent> source)
    {
        var result = new List<Agent>();
        await foreach (var agent in source)
            result.Add(agent);
        return result;
    }

    [Fact]
    public async Task StreamRoutableAgentsAsync_ShouldYieldOnlyAvailableAndBusy_WhenMixedStates()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        var available = MakeAgent(tenant, AgentState.Available);
        var busy = MakeAgent(tenant, AgentState.Busy);
        var offline = MakeAgent(tenant, AgentState.Offline);
        var onBreak = MakeAgent(tenant, AgentState.Break);
        await store.SaveAsync(available, CancellationToken.None);
        await store.SaveAsync(busy, CancellationToken.None);
        await store.SaveAsync(offline, CancellationToken.None);
        await store.SaveAsync(onBreak, CancellationToken.None);

        var routable = await CollectAsync(store.StreamRoutableAgentsAsync(CancellationToken.None));

        routable.Select(a => a.AgentId).Should().BeEquivalentTo(new[] { available.AgentId, busy.AgentId });
    }

    [Fact]
    public async Task StreamRoutableAgentsAsync_ShouldYieldAcrossTenants_WhenMultipleTenants()
    {
        var store = new InMemoryAgentStore();
        var agentA = MakeAgent(new TenantId("tenant-a"), AgentState.Available);
        var agentB = MakeAgent(new TenantId("tenant-b"), AgentState.Busy);
        await store.SaveAsync(agentA, CancellationToken.None);
        await store.SaveAsync(agentB, CancellationToken.None);

        var routable = await CollectAsync(store.StreamRoutableAgentsAsync(CancellationToken.None));

        routable.Select(a => a.AgentId).Should().BeEquivalentTo(new[] { agentA.AgentId, agentB.AgentId });
    }

    [Fact]
    public async Task StreamRoutableAgentsAsync_ShouldYieldNothing_WhenNoRoutableAgents()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        await store.SaveAsync(MakeAgent(tenant, AgentState.Offline), CancellationToken.None);
        await store.SaveAsync(MakeAgent(tenant, AgentState.Lunch), CancellationToken.None);

        var routable = await CollectAsync(store.StreamRoutableAgentsAsync(CancellationToken.None));

        routable.Should().BeEmpty();
    }
}
