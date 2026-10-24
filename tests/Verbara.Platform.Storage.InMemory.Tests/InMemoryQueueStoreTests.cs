using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// dialer-places-calls-end-to-end (design D11 point 3): the tenant-scoped lookup of a queue by its exact name, which
/// the Stasis consumer uses to continue an answered campaign leg into the campaign's target queue. It must find a
/// target that sorts past the first 500 queues (no page of <see cref="IQueueStore.ListAsync"/>), never another
/// tenant's queue, and prefer the active queue when two share the name (names are not unique).
/// </summary>
public sealed class InMemoryQueueStoreTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Beta = new("beta");

    private static Queue MakeQueue(TenantId tenant, string id, string name, bool isActive = true) =>
        new()
        {
            QueueId = EntityId.From(id),
            TenantId = tenant,
            Name = name,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task GetByNameAsync_ShouldReturnTheTenantsQueue_WhenTheNameMatches()
    {
        var store = new InMemoryQueueStore();
        // 500 queues of acme whose names sort before the target, then the target: 501 in all.
        for (var i = 0; i < 500; i++)
            await store.SaveAsync(MakeQueue(Acme, $"q-acme-{i:D3}", $"a-{i:D3}"), CancellationToken.None);
        await store.SaveAsync(MakeQueue(Acme, "q-acme-sales", "sales"), CancellationToken.None);
        await store.SaveAsync(MakeQueue(Beta, "q-beta-sales", "sales"), CancellationToken.None);

        var found = await store.GetByNameAsync(Acme, "sales", CancellationToken.None);

        found.Should().NotBeNull("acme holds a queue named sales, past the first 500 by name");
        found!.TenantId.Should().Be(Acme);
        found.QueueId.Should().Be(EntityId.From("q-acme-sales"));
    }

    [Fact]
    public async Task GetByNameAsync_ShouldPreferTheActiveQueue_WhenTwoShareTheName()
    {
        var store = new InMemoryQueueStore();
        // The inactive one has the lower id, so neither id order nor insertion order picks the active one.
        await store.SaveAsync(MakeQueue(Acme, "q-001", "sales", isActive: false), CancellationToken.None);
        await store.SaveAsync(MakeQueue(Acme, "q-002", "sales"), CancellationToken.None);

        var found = await store.GetByNameAsync(Acme, "sales", CancellationToken.None);

        found.Should().NotBeNull("acme holds two queues named sales");
        found!.QueueId.Should().Be(EntityId.From("q-002"), "the active queue wins over an inactive one of the same name");
        found.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetByNameAsync_ShouldReturnNull_WhenNoQueueHasTheName()
    {
        var store = new InMemoryQueueStore();
        await store.SaveAsync(MakeQueue(Acme, "q-support", "support"), CancellationToken.None);
        await store.SaveAsync(MakeQueue(Beta, "q-beta-sales", "sales"), CancellationToken.None);

        var found = await store.GetByNameAsync(Acme, "sales", CancellationToken.None);

        found.Should().BeNull();
    }
}
