using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering-follow-ups L16 (design D2, "Measure first"): the widened-window control for
/// <see cref="AgentAccountStatusLookupExtensions.ListAllAsync"/> against a real Postgres. One agent of the first page
/// is deleted right after that page is read; every agent that existed for the whole enumeration must still be
/// returned, exactly once. The assertion is on membership, never on a count.
/// </summary>
public class PostgresAgentEnumerationControlTests : IClassFixture<AgentStoreAutoAnswerFixture>, IAsyncLifetime
{
    private const int AgentCount = 1200;
    private static readonly TenantId Tenant = new("acme");

    private readonly AgentStoreAutoAnswerFixture _fixture;

    public PostgresAgentEnumerationControlTests(AgentStoreAutoAnswerFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListAllAsync_ShouldReturnEverySurvivorOnce_WhenAFirstPageAgentIsDeletedAfterTheFirstPageIsRead()
    {
        // Lower-case ASCII ids: their database-collation order and their ordinal order agree.
        var ids = new List<EntityId>(AgentCount);
        await using (var conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            for (var i = 0; i < AgentCount; i++)
            {
                var id = EntityId.From($"agent-{i:D4}");
                ids.Add(id);
                await PostgresAgentStore.InsertAsync(conn, tx, new Agent
                {
                    AgentId = id,
                    TenantId = Tenant,
                    UserId = EntityId.From($"user-{i:D4}"),
                    DisplayName = $"Agent agent-{i:D4}",
                    State = AgentState.Offline,
                    CreatedAt = DateTimeOffset.UtcNow,
                }, CancellationToken.None);
            }
            await tx.CommitAsync();
        }
        var store = new DeleteAfterFirstPageAgentStore(new PostgresAgentStore(_fixture.DataSource));

        var result = await store.ListAllAsync(Tenant, CancellationToken.None);

        store.DeletedAgentId.Should().NotBeNull("the control must have deleted a first-page agent");
        var survivors = ids.Where(id => id != store.DeletedAgentId).ToList();
        var returned = result.Select(a => a.AgentId).ToList();
        returned.Should().OnlyHaveUniqueItems();
        returned.Should().Contain(survivors);
    }
}
