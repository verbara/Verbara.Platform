using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Realtime;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// ADR-0012 Ola-3 — DI-resolution tests for <see cref="RealtimeSyncingStoresExtensions.AddRealtimeSyncingStores"/>.
/// Proves the registrar wires the decorator when <see cref="IRealtimeSyncService"/> is registered and
/// falls back to the undecorated concrete store when it is not — exercising the registrar's keyed-inner
/// factory lambdas for all three stores.
/// </summary>
public sealed class RealtimeSyncingStoresRegistrarTests
{
    [Fact]
    public void Resolve_ShouldReturnDecorators_WhenRealtimeSyncServiceRegistered()
    {
        var provider = BuildProvider(withRealtime: true);

        provider.GetRequiredService<IQueueStore>().Should().BeOfType<RealtimeSyncingQueueStore>();
        provider.GetRequiredService<IAgentStore>().Should().BeOfType<RealtimeSyncingAgentStore>();
        provider.GetRequiredService<IQueueMembershipStore>().Should().BeOfType<RealtimeSyncingQueueMembershipStore>();
    }

    [Fact]
    public void Resolve_ShouldPassThroughToConcreteStores_WhenRealtimeSyncServiceAbsent()
    {
        var provider = BuildProvider(withRealtime: false);

        // Fully-qualify the membership store: the test project also declares an
        // InMemoryQueueMembershipStore (used by other factories), so the short name is ambiguous.
        provider.GetRequiredService<IQueueStore>().Should().BeOfType<InMemoryQueueStore>();
        provider.GetRequiredService<IAgentStore>().Should().BeOfType<InMemoryAgentStore>();
        provider.GetRequiredService<IQueueMembershipStore>()
            .Should().BeOfType<Verbara.Platform.Storage.InMemory.InMemoryQueueMembershipStore>();
    }

    [Fact]
    public void Resolve_ShouldExposeUndecoratedInner_ViaKey()
    {
        // R3 — the membership decorator resolves queue/agent name lookups via the KEYED inners,
        // which must always be the undecorated concrete store (never the decorator).
        var provider = BuildProvider(withRealtime: true);

        provider.GetRequiredKeyedService<IQueueStore>(RealtimeSyncingStoresExtensions.QueueStoreInner)
            .Should().BeOfType<InMemoryQueueStore>();
        provider.GetRequiredKeyedService<IAgentStore>(RealtimeSyncingStoresExtensions.AgentStoreInner)
            .Should().BeOfType<InMemoryAgentStore>();
        provider.GetRequiredKeyedService<IQueueMembershipStore>(RealtimeSyncingStoresExtensions.QueueMembershipStoreInner)
            .Should().BeOfType<Verbara.Platform.Storage.InMemory.InMemoryQueueMembershipStore>();
    }

    [Fact]
    public async Task MembershipDecorator_ShouldConvergeUnderTheSingletonPauseCoordinator()
    {
        // queue-members-stay-unpaused-across-reconcile — the membership decorator must take the SAME
        // per-agent lock as RealtimeStateBridge and the reconciler, i.e. the DI singleton.
        var provider = BuildProvider(withRealtime: true);
        var coordinator = provider.GetRequiredService<AgentPauseCoordinator>();
        provider.GetRequiredService<AgentPauseCoordinator>().Should().BeSameAs(coordinator);
        var tenant = new Verbara.Platform.Core.TenantId("t-reg");
        var queue = new Queue
        {
            QueueId = Verbara.Platform.Core.EntityId.New(), TenantId = tenant, Name = "q-reg",
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        var agent = new Agent
        {
            AgentId = Verbara.Platform.Core.EntityId.New(), TenantId = tenant, UserId = Verbara.Platform.Core.EntityId.New(),
            DisplayName = "Registrar Agent", State = AgentState.Available, CreatedAt = DateTimeOffset.UtcNow,
        };
        await provider.GetRequiredKeyedService<IQueueStore>(RealtimeSyncingStoresExtensions.QueueStoreInner)
            .SaveAsync(queue, CancellationToken.None);
        await provider.GetRequiredKeyedService<IAgentStore>(RealtimeSyncingStoresExtensions.AgentStoreInner)
            .SaveAsync(agent, CancellationToken.None);
        var membership = new QueueMembership
        {
            TenantId = tenant, QueueId = queue.QueueId, AgentId = agent.AgentId,
            Source = MembershipSource.Manual, CreatedAt = DateTimeOffset.UtcNow,
        };

        Task save;
        using (await coordinator.AcquireAsync(agent.AgentId.Value, CancellationToken.None))
        {
            save = provider.GetRequiredService<IQueueMembershipStore>().SaveAsync(membership, CancellationToken.None);
            // No wall-clock wait: a contended SemaphoreSlim.WaitAsync returns an incomplete task synchronously.
            save.IsCompleted.Should().BeFalse("the decorator's convergence must wait on the singleton's lock");
        }

        await save.WaitAsync(TimeSpan.FromSeconds(5));
        await provider.GetRequiredService<IRealtimeSyncService>().Received(1)
            .SyncAgentPausedAsync("t-reg", agent.AgentId.Value, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AddRealtimeSyncingStores_ShouldThrow_WhenStorageNotRegisteredFirst()
    {
        var services = new ServiceCollection();

        var act = () => services.AddRealtimeSyncingStores();

        act.Should().Throw<InvalidOperationException>(
            "the registrar must run AFTER AddPostgresStorage / AddInMemoryStorage (R2)");
    }

    private static ServiceProvider BuildProvider(bool withRealtime)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryStorage();
        if (withRealtime)
            services.AddSingleton(Substitute.For<IRealtimeSyncService>());
        services.AddRealtimeSyncingStores();
        return services.BuildServiceProvider();
    }
}
