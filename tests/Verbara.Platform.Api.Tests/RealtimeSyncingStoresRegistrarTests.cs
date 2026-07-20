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
