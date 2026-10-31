using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// The widened-window control for <see cref="QueueStoreExtensions.ListAllAsync"/> on the InMemory store. One queue of
/// the first page is deleted right after that page is read; every queue that existed for the whole enumeration must
/// still be returned, exactly once. The assertion is on membership, never on a count.
/// </summary>
public sealed class InMemoryQueueEnumerationControlTests
{
    private const int QueueCount = 1200;
    private static readonly TenantId Tenant = new("tenant-enum");

    [Fact]
    public async Task ListAllAsync_ShouldReturnEverySurvivorOnce_WhenAFirstPageQueueIsDeletedAfterTheFirstPageIsRead()
    {
        var inner = new InMemoryQueueStore();
        var ids = new List<EntityId>(QueueCount);
        for (var i = 0; i < QueueCount; i++)
        {
            var id = EntityId.From($"queue-{i:D4}");
            ids.Add(id);
            await inner.SaveAsync(new Queue
            {
                QueueId = id,
                TenantId = Tenant,
                Name = $"name-{i:D4}",
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }
        var store = new DeleteAfterFirstPageQueueStore(inner);

        var result = await store.ListAllAsync(Tenant, CancellationToken.None);

        store.DeletedQueueId.Should().NotBeNull("the control must have deleted a first-page queue");
        var survivors = ids.Where(id => id != store.DeletedQueueId).ToList();
        var returned = result.Select(q => q.QueueId).ToList();
        returned.Should().OnlyHaveUniqueItems();
        returned.Should().Contain(survivors);
    }
}
