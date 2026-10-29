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
    public async Task GetByIdsAsync_ShouldReturnRequestedSubset_WhenMultipleSaved()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        var a1 = MakeAgent(tenant, AgentState.Available);
        var a2 = MakeAgent(tenant, AgentState.Busy);
        var a3 = MakeAgent(tenant, AgentState.Offline);
        await store.SaveAsync(a1, CancellationToken.None);
        await store.SaveAsync(a2, CancellationToken.None);
        await store.SaveAsync(a3, CancellationToken.None);

        var result = await store.GetByIdsAsync(tenant, [a1.AgentId, a3.AgentId], CancellationToken.None);

        result.Select(a => a.AgentId).Should().BeEquivalentTo(new[] { a1.AgentId, a3.AgentId });
    }

    [Fact]
    public async Task GetByIdsAsync_ShouldIsolateByTenant_WhenSameIdSetAcrossTenants()
    {
        var store = new InMemoryAgentStore();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        var inA = MakeAgent(tenantA, AgentState.Available);
        var inB = MakeAgent(tenantB, AgentState.Available);
        await store.SaveAsync(inA, CancellationToken.None);
        await store.SaveAsync(inB, CancellationToken.None);

        var result = await store.GetByIdsAsync(tenantA, [inA.AgentId, inB.AgentId], CancellationToken.None);

        result.Should().ContainSingle().Which.AgentId.Should().Be(inA.AgentId);
    }

    [Fact]
    public async Task GetByIdsAsync_ShouldReturnEmpty_WhenEmptyCollection()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        await store.SaveAsync(MakeAgent(tenant, AgentState.Available), CancellationToken.None);

        var result = await store.GetByIdsAsync(tenant, [], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByIdsAsync_ShouldOmitUnknownIds_WhenSomeNotPresent()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        var known = MakeAgent(tenant, AgentState.Available);
        await store.SaveAsync(known, CancellationToken.None);

        var result = await store.GetByIdsAsync(tenant, [known.AgentId, EntityId.New()], CancellationToken.None);

        result.Should().ContainSingle().Which.AgentId.Should().Be(known.AgentId);
    }

    [Fact]
    public async Task GetByIdsAsync_ShouldReturnEachAgentOnce_WhenDuplicateIdsRequested()
    {
        var store = new InMemoryAgentStore();
        var tenant = new TenantId("tenant-1");
        var agent = MakeAgent(tenant, AgentState.Available);
        await store.SaveAsync(agent, CancellationToken.None);

        var result = await store.GetByIdsAsync(tenant, [agent.AgentId, agent.AgentId], CancellationToken.None);

        result.Should().ContainSingle().Which.AgentId.Should().Be(agent.AgentId);
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

    // licensed-agent-metering-follow-ups (design D2) — keyset page in ordinal agent_id order.
    private static Agent MakeAgentWithId(TenantId tenantId, string agentId) =>
        new()
        {
            AgentId = EntityId.From(agentId),
            TenantId = tenantId,
            UserId = EntityId.New(),
            DisplayName = "Agent " + agentId,
            State = AgentState.Offline,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static async Task<InMemoryAgentStore> SeedKeysetStoreAsync(TenantId tenant, TenantId other)
    {
        var store = new InMemoryAgentStore();
        // Saved out of order; "B" sorts before "a" ordinally, unlike a culture-aware comparison.
        foreach (var id in new[] { "agent-d", "agent-a", "agent-B", "agent-c", "agent-b" })
            await store.SaveAsync(MakeAgentWithId(tenant, id), CancellationToken.None);
        // The other tenant's ids interleave with this tenant's, so a missing tenant filter would show.
        foreach (var id in new[] { "agent-0", "agent-bb", "agent-cc", "agent-z" })
            await store.SaveAsync(MakeAgentWithId(other, id), CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnThisTenantsAgentsInOrdinalOrder_WhenAfterIsNull()
    {
        var tenant = new TenantId("tenant-a");
        var store = await SeedKeysetStoreAsync(tenant, new TenantId("tenant-b"));

        var page = await store.ListPageAfterAsync(tenant, null, 100, CancellationToken.None);

        page.Select(a => a.AgentId.Value).Should().Equal("agent-B", "agent-a", "agent-b", "agent-c", "agent-d");
        page.Should().OnlyContain(a => a.TenantId == tenant);
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldExcludeTheAfterBound_WhenAfterIsAnExistingAgent()
    {
        var tenant = new TenantId("tenant-a");
        var store = await SeedKeysetStoreAsync(tenant, new TenantId("tenant-b"));

        var page = await store.ListPageAfterAsync(tenant, EntityId.From("agent-b"), 100, CancellationToken.None);

        page.Select(a => a.AgentId.Value).Should().Equal("agent-c", "agent-d");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldStartAfterTheBound_WhenAfterIsNotAnAgent()
    {
        var tenant = new TenantId("tenant-a");
        var store = await SeedKeysetStoreAsync(tenant, new TenantId("tenant-b"));

        var page = await store.ListPageAfterAsync(tenant, EntityId.From("agent-bb"), 100, CancellationToken.None);

        page.Select(a => a.AgentId.Value).Should().Equal("agent-c", "agent-d");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnAtMostLimit_WhenMoreAgentsFollow()
    {
        var tenant = new TenantId("tenant-a");
        var store = await SeedKeysetStoreAsync(tenant, new TenantId("tenant-b"));

        var first = await store.ListPageAfterAsync(tenant, null, 2, CancellationToken.None);
        var second = await store.ListPageAfterAsync(tenant, first[^1].AgentId, 2, CancellationToken.None);
        var third = await store.ListPageAfterAsync(tenant, second[^1].AgentId, 2, CancellationToken.None);

        first.Select(a => a.AgentId.Value).Should().Equal("agent-B", "agent-a");
        second.Select(a => a.AgentId.Value).Should().Equal("agent-b", "agent-c");
        third.Select(a => a.AgentId.Value).Should().Equal("agent-d");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnEmpty_WhenAfterIsTheLastAgent()
    {
        var tenant = new TenantId("tenant-a");
        var store = await SeedKeysetStoreAsync(tenant, new TenantId("tenant-b"));

        var page = await store.ListPageAfterAsync(tenant, EntityId.From("agent-d"), 100, CancellationToken.None);

        page.Should().BeEmpty();
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnNoOtherTenantsAgent_WhenTheTenantHasNone()
    {
        var store = await SeedKeysetStoreAsync(new TenantId("tenant-a"), new TenantId("tenant-b"));

        var page = await store.ListPageAfterAsync(new TenantId("tenant-c"), null, 100, CancellationToken.None);

        page.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListPageAfterAsync_ShouldThrow_WhenLimitIsNotPositive(int limit)
    {
        var store = new InMemoryAgentStore();

        var act = () => store.ListPageAfterAsync(new TenantId("tenant-a"), null, limit, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
