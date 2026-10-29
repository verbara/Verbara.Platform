using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// licensed-agent-metering-follow-ups L16 (design D2, "Measure first"): the widened-window control for
/// <see cref="AgentAccountStatusLookupExtensions.ListAllAsync"/> on the InMemory store. One agent of the first page
/// is deleted right after that page is read; every agent that existed for the whole enumeration must still be
/// returned, exactly once. The assertion is on membership, never on a count.
/// </summary>
public sealed class InMemoryAgentEnumerationControlTests
{
    private const int AgentCount = 1200;
    private static readonly TenantId Tenant = new("tenant-enum");

    [Fact]
    public async Task ListAllAsync_ShouldReturnEverySurvivorOnce_WhenAFirstPageAgentIsDeletedAfterTheFirstPageIsRead()
    {
        var inner = new InMemoryAgentStore();
        var ids = new List<EntityId>(AgentCount);
        for (var i = 0; i < AgentCount; i++)
        {
            var id = EntityId.From($"agent-{i:D4}");
            ids.Add(id);
            await inner.SaveAsync(new Agent
            {
                AgentId = id,
                TenantId = Tenant,
                UserId = EntityId.From($"user-{i:D4}"),
                DisplayName = $"Agent agent-{i:D4}",
                State = AgentState.Offline,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }
        var store = new DeleteAfterFirstPageAgentStore(inner);

        var result = await store.ListAllAsync(Tenant, CancellationToken.None);

        store.DeletedAgentId.Should().NotBeNull("the control must have deleted a first-page agent");
        var survivors = ids.Where(id => id != store.DeletedAgentId).ToList();
        var returned = result.Select(a => a.AgentId).ToList();
        returned.Should().OnlyHaveUniqueItems();
        returned.Should().Contain(survivors);
    }
}
