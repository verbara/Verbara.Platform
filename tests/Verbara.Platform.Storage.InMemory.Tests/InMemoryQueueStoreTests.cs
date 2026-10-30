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

    private static async Task<InMemoryQueueStore> SeedKeysetStoreAsync()
    {
        var store = new InMemoryQueueStore();
        // Named against the id order, so the page order can only come from the id. "queue-B" sorts first: Ordinal.
        foreach (var (id, name) in new[]
                 {
                     ("queue-d", "w"), ("queue-a", "z"), ("queue-B", "u"), ("queue-c", "x"), ("queue-b", "y"),
                 })
        {
            await store.SaveAsync(MakeQueue(Acme, id, name), CancellationToken.None);
        }

        // The other tenant's ids interleave with this tenant's, so a missing tenant filter would show.
        foreach (var id in new[] { "queue-0", "queue-bb", "queue-cc", "queue-z" })
            await store.SaveAsync(MakeQueue(Beta, id, id), CancellationToken.None);

        return store;
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnThisTenantsQueuesInOrdinalOrder_WhenAfterIsNull()
    {
        var store = await SeedKeysetStoreAsync();

        var page = await store.ListPageAfterAsync(Acme, null, 100, CancellationToken.None);

        page.Select(q => q.QueueId.Value).Should().Equal("queue-B", "queue-a", "queue-b", "queue-c", "queue-d");
        page.Should().OnlyContain(q => q.TenantId == Acme);
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldExcludeTheAfterBound_WhenAfterIsAnExistingQueue()
    {
        var store = await SeedKeysetStoreAsync();

        var page = await store.ListPageAfterAsync(Acme, EntityId.From("queue-b"), 100, CancellationToken.None);

        page.Select(q => q.QueueId.Value).Should().Equal("queue-c", "queue-d");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldStartAfterTheBound_WhenAfterIsNotAQueue()
    {
        var store = await SeedKeysetStoreAsync();

        var page = await store.ListPageAfterAsync(Acme, EntityId.From("queue-bb"), 100, CancellationToken.None);

        page.Select(q => q.QueueId.Value).Should().Equal("queue-c", "queue-d");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnAtMostLimit_WhenMoreQueuesFollow()
    {
        var store = await SeedKeysetStoreAsync();

        var first = await store.ListPageAfterAsync(Acme, null, 2, CancellationToken.None);
        var second = await store.ListPageAfterAsync(Acme, first[^1].QueueId, 2, CancellationToken.None);
        var third = await store.ListPageAfterAsync(Acme, second[^1].QueueId, 2, CancellationToken.None);

        first.Select(q => q.QueueId.Value).Should().Equal("queue-B", "queue-a");
        second.Select(q => q.QueueId.Value).Should().Equal("queue-b", "queue-c");
        third.Select(q => q.QueueId.Value).Should().Equal("queue-d");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnEmpty_WhenAfterIsTheLastQueue()
    {
        var store = await SeedKeysetStoreAsync();

        var page = await store.ListPageAfterAsync(Acme, EntityId.From("queue-d"), 100, CancellationToken.None);

        page.Should().BeEmpty();
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldThrow_WhenLimitIsNotPositive()
    {
        var store = await SeedKeysetStoreAsync();

        var act = () => store.ListPageAfterAsync(Acme, null, 0, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
