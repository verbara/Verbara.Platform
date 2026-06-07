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

    [Fact]
    public async Task StreamPendingPauseAgentsAsync_ShouldYieldOnlyAgentsWithPending_WhenMixedStates()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        var pendingA = MakeAgent(tenant, AgentState.Busy);
        pendingA.PendingState = AgentState.Break;
        var pendingB = MakeAgent(tenant, AgentState.ACW);
        pendingB.PendingState = AgentState.Lunch;
        var noPending = MakeAgent(tenant, AgentState.Available);
        await store.SaveAsync(pendingA, CancellationToken.None);
        await store.SaveAsync(pendingB, CancellationToken.None);
        await store.SaveAsync(noPending, CancellationToken.None);

        var pending = await CollectAsync(store.StreamPendingPauseAgentsAsync(CancellationToken.None));

        pending.Select(a => a.AgentId).Should().BeEquivalentTo(new[] { pendingA.AgentId, pendingB.AgentId });
    }

    [Fact]
    public async Task StreamPendingPauseAgentsAsync_ShouldYieldNothing_WhenNoPending()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        await store.SaveAsync(MakeAgent(tenant, AgentState.Available), CancellationToken.None);
        await store.SaveAsync(MakeAgent(tenant, AgentState.Busy), CancellationToken.None);

        var pending = await CollectAsync(store.StreamPendingPauseAgentsAsync(CancellationToken.None));

        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamOfflineAgentsAsync_ShouldYieldOnlyOffline_WhenMixedStates()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        var offlineA = MakeAgent(tenant, AgentState.Offline);
        var offlineB = MakeAgent(tenant, AgentState.Offline);
        var available = MakeAgent(tenant, AgentState.Available);
        var busy = MakeAgent(tenant, AgentState.Busy);
        var onBreak = MakeAgent(tenant, AgentState.Break);
        await store.SaveAsync(offlineA, CancellationToken.None);
        await store.SaveAsync(offlineB, CancellationToken.None);
        await store.SaveAsync(available, CancellationToken.None);
        await store.SaveAsync(busy, CancellationToken.None);
        await store.SaveAsync(onBreak, CancellationToken.None);

        var offline = await CollectAsync(store.StreamOfflineAgentsAsync(CancellationToken.None));

        offline.Select(a => a.AgentId).Should().BeEquivalentTo(new[] { offlineA.AgentId, offlineB.AgentId });
    }

    [Fact]
    public async Task StreamOfflineAgentsAsync_ShouldYieldAcrossTenants_WhenMultipleTenants()
    {
        var store = new InMemoryAgentStore();
        var agentA = MakeAgent(new TenantId("tenant-a"), AgentState.Offline);
        var agentB = MakeAgent(new TenantId("tenant-b"), AgentState.Offline);
        await store.SaveAsync(agentA, CancellationToken.None);
        await store.SaveAsync(agentB, CancellationToken.None);

        var offline = await CollectAsync(store.StreamOfflineAgentsAsync(CancellationToken.None));

        offline.Select(a => a.AgentId).Should().BeEquivalentTo(new[] { agentA.AgentId, agentB.AgentId });
    }

    [Fact]
    public async Task StreamOfflineAgentsAsync_ShouldYieldNothing_WhenNoneOffline()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        await store.SaveAsync(MakeAgent(tenant, AgentState.Available), CancellationToken.None);
        await store.SaveAsync(MakeAgent(tenant, AgentState.Busy), CancellationToken.None);

        var offline = await CollectAsync(store.StreamOfflineAgentsAsync(CancellationToken.None));

        offline.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ShouldRoundTripPendingFields_WhenPendingSet()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        var since = DateTimeOffset.UtcNow;
        var agent = MakeAgent(tenant, AgentState.Busy);
        agent.PendingState = AgentState.Lunch;
        agent.PendingReason = "lunch";
        agent.PendingSince = since;
        await store.SaveAsync(agent, CancellationToken.None);

        var loaded = await store.GetByIdAsync(tenant, agent.AgentId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.PendingState.Should().Be(AgentState.Lunch);
        loaded.PendingReason.Should().Be("lunch");
        loaded.PendingSince.Should().Be(since);
        loaded.HasPendingPause.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_ShouldRoundTripOfflineSince_WhenSet()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        var offlineSince = DateTimeOffset.UtcNow;
        var agent = MakeAgent(tenant, AgentState.Offline);
        agent.OfflineSince = offlineSince;
        await store.SaveAsync(agent, CancellationToken.None);

        var loaded = await store.GetByIdAsync(tenant, agent.AgentId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.OfflineSince.Should().Be(offlineSince);
    }
}
