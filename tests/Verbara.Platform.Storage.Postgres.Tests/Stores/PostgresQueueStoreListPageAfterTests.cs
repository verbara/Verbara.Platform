using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="PostgresQueueStore.ListPageAfterAsync"/> against a real Postgres: a keyset page on the primary key
/// <c>(tenant_id, queue_id)</c>, ascending, with an exclusive <c>after</c> bound, at most <c>limit</c> rows, and only
/// the named tenant's queues. Each test uses unique tenant ids. Same cases as <c>InMemoryQueueStoreTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresQueueStoreListPageAfterTests : IClassFixture<MigrationsFixture>
{
    // Lower-case ASCII ids: their database-collation order and their ordinal order agree.
    private static readonly string[] TenantIdsAscending = ["queue-a", "queue-b", "queue-c", "queue-d", "queue-e"];

    private readonly PostgresQueueStore _sut;

    public PostgresQueueStoreListPageAfterTests(MigrationsFixture fixture)
        => _sut = new PostgresQueueStore(fixture.DataSource);

    private static Queue MakeQueue(TenantId tenant, string id, string name) => new()
    {
        QueueId = EntityId.From(id),
        TenantId = tenant,
        Name = name,
        CreatedAt = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero),
    };

    private async Task<TenantId> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new TenantId($"acme-{suffix}");
        var other = new TenantId($"globex-{suffix}");

        // Inserted out of order and named against the id order, so the page order can only come from ORDER BY queue_id.
        foreach (var (id, name) in new[]
                 {
                     ("queue-d", "w"), ("queue-a", "z"), ("queue-e", "v"), ("queue-c", "x"), ("queue-b", "y"),
                 })
        {
            await _sut.SaveAsync(MakeQueue(tenant, id, name), CancellationToken.None);
        }

        // The other tenant's ids interleave with this tenant's, so a missing tenant filter would show.
        foreach (var id in new[] { "queue-0", "queue-bb", "queue-cc", "queue-z" })
            await _sut.SaveAsync(MakeQueue(other, id, id), CancellationToken.None);

        return tenant;
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnThisTenantsQueuesAscending_WhenAfterIsNull()
    {
        var tenant = await SeedAsync();

        var page = await _sut.ListPageAfterAsync(tenant, null, 100, CancellationToken.None);

        page.Select(q => q.QueueId.Value).Should().Equal(TenantIdsAscending);
        page.Should().OnlyContain(q => q.TenantId == tenant);
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldExcludeTheAfterBound_WhenAfterIsAnExistingQueue()
    {
        var tenant = await SeedAsync();

        var page = await _sut.ListPageAfterAsync(tenant, EntityId.From("queue-b"), 100, CancellationToken.None);

        page.Select(q => q.QueueId.Value).Should().Equal("queue-c", "queue-d", "queue-e");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldStartAfterTheBound_WhenAfterIsNotAQueue()
    {
        var tenant = await SeedAsync();

        var page = await _sut.ListPageAfterAsync(tenant, EntityId.From("queue-bb"), 100, CancellationToken.None);

        page.Select(q => q.QueueId.Value).Should().Equal("queue-c", "queue-d", "queue-e");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnAtMostLimit_WhenMoreQueuesFollow()
    {
        var tenant = await SeedAsync();

        var first = await _sut.ListPageAfterAsync(tenant, null, 2, CancellationToken.None);
        var second = await _sut.ListPageAfterAsync(tenant, first[^1].QueueId, 2, CancellationToken.None);
        var third = await _sut.ListPageAfterAsync(tenant, second[^1].QueueId, 2, CancellationToken.None);

        first.Select(q => q.QueueId.Value).Should().Equal("queue-a", "queue-b");
        second.Select(q => q.QueueId.Value).Should().Equal("queue-c", "queue-d");
        third.Select(q => q.QueueId.Value).Should().Equal("queue-e");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnEmpty_WhenAfterIsTheLastQueue()
    {
        var tenant = await SeedAsync();

        var page = await _sut.ListPageAfterAsync(tenant, EntityId.From("queue-e"), 100, CancellationToken.None);

        page.Should().BeEmpty();
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldThrow_WhenLimitIsNotPositive()
    {
        var tenant = await SeedAsync();

        var act = () => _sut.ListPageAfterAsync(tenant, null, 0, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
