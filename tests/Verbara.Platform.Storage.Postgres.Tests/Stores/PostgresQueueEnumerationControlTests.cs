using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// The widened-window control for <see cref="QueueStoreExtensions.ListAllAsync"/> against a real Postgres. One queue
/// of the first page is deleted right after that page is read; every queue that existed for the whole enumeration
/// must still be returned, exactly once. The assertion is on membership, never on a count. Two queues share a name
/// across the page boundary, because names are not unique and the row-offset page orders by name alone.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresQueueEnumerationControlTests : IClassFixture<MigrationsFixture>
{
    private const int QueueCount = 1200;

    private readonly MigrationsFixture _fixture;

    public PostgresQueueEnumerationControlTests(MigrationsFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListAllAsync_ShouldReturnEverySurvivorOnce_WhenAFirstPageQueueIsDeletedAfterTheFirstPageIsRead()
    {
        var tenant = new TenantId($"acme-{Guid.NewGuid():N}");
        var inner = new PostgresQueueStore(_fixture.DataSource);

        // Lower-case ASCII ids: their database-collation order and their ordinal order agree.
        var ids = new List<EntityId>(QueueCount);
        for (var i = 0; i < QueueCount; i++)
        {
            var id = EntityId.From($"queue-{i:D4}");
            ids.Add(id);
            await inner.SaveAsync(new Queue
            {
                QueueId = id,
                TenantId = tenant,
                Name = i is 999 or 1000 ? "shared" : $"name-{i:D4}",
                CreatedAt = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero),
            }, CancellationToken.None);
        }
        var store = new DeleteAfterFirstPageQueueStore(inner);

        var result = await store.ListAllAsync(tenant, CancellationToken.None);

        store.DeletedQueueId.Should().NotBeNull("the control must have deleted a first-page queue");
        var survivors = ids.Where(id => id != store.DeletedQueueId).ToList();
        var returned = result.Select(q => q.QueueId).ToList();
        returned.Should().OnlyHaveUniqueItems();
        returned.Should().Contain(survivors);
    }
}
