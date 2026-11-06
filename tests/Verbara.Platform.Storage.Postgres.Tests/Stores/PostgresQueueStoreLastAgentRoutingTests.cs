using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// conversations-return-to-last-agent task 1.3 (design D2): <c>queue_configs.last_agent_routing_enabled</c> is set by SQL
/// only. Every read that builds a <see cref="Queue"/> carries it; no save writes it, so a new queue reads <c>NULL</c> and a
/// save of an entity loaded before an operator changed the column keeps the column's value (audit 2026-11-06 F3: the
/// stale-entity case is the one a write-back of the entity's own value cannot pass). Real embedded migrations through
/// <see cref="MigrationsFixture"/>; each test uses its own tenant.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresQueueStoreLastAgentRoutingTests : IClassFixture<MigrationsFixture>
{
    private readonly MigrationsFixture _fixture;
    private readonly PostgresQueueStore _store;

    public PostgresQueueStoreLastAgentRoutingTests(MigrationsFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresQueueStore(fixture.DataSource);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task GetByIdAsync_ShouldReadTheValueSetBySql_WhenAnOperatorSetsTheColumn(bool? value)
    {
        var queue = await SaveNewQueueAsync("Support");
        await SetBySqlAsync(queue, value);

        var loaded = await _store.GetByIdAsync(queue.TenantId, queue.QueueId, CancellationToken.None);

        loaded!.LastAgentRoutingEnabled.Should().Be(value);
    }

    [Fact]
    public async Task EveryRead_ShouldCarryTheColumn_WhenItBuildsAQueue()
    {
        var queue = await SaveNewQueueAsync($"Sales-{Guid.NewGuid():N}");
        await SetBySqlAsync(queue, true);

        (await _store.GetByNameAsync(queue.TenantId, queue.Name, CancellationToken.None))!
            .LastAgentRoutingEnabled.Should().BeTrue();
        (await _store.ListAsync(queue.TenantId, new PagedQuery(), CancellationToken.None)).Items
            .Should().ContainSingle().Which.LastAgentRoutingEnabled.Should().BeTrue();
        (await _store.ListPageAfterAsync(queue.TenantId, null, 10, CancellationToken.None))
            .Should().ContainSingle().Which.LastAgentRoutingEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_ShouldLeaveTheColumnNull_WhenItCreatesAQueue()
    {
        var queue = await SaveNewQueueAsync("Billing");

        (await ColumnAsync(queue)).Should().Be(DBNull.Value, "a new queue follows the host");
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepTheSqlValue_WhenItSavesAQueueLoadedBeforeTheSqlChange()
    {
        var queue = await SaveNewQueueAsync("Retention");
        var loaded = (await _store.GetByIdAsync(queue.TenantId, queue.QueueId, CancellationToken.None))!;
        loaded.LastAgentRoutingEnabled.Should().BeNull();

        await SetBySqlAsync(queue, false);
        loaded.Name = "Retention (renamed)";
        await _store.SaveAsync(loaded, CancellationToken.None);

        var reread = (await _store.GetByIdAsync(queue.TenantId, queue.QueueId, CancellationToken.None))!;
        reread.Name.Should().Be("Retention (renamed)", "the save lands its other fields");
        reread.LastAgentRoutingEnabled.Should().BeFalse("a save of a stale entity must not write the column back");
    }

    private async Task<Queue> SaveNewQueueAsync(string name)
    {
        var queue = new Queue
        {
            QueueId = EntityId.New(),
            TenantId = new TenantId($"t-{Guid.NewGuid():N}"),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _store.SaveAsync(queue, CancellationToken.None);
        return queue;
    }

    private async Task SetBySqlAsync(Queue queue, bool? value)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            "UPDATE queue_configs SET last_agent_routing_enabled = @Value WHERE tenant_id = @TenantId AND queue_id = @QueueId");
        cmd.Parameters.Add(new NpgsqlParameter("Value", NpgsqlDbType.Boolean) { Value = value.HasValue ? value.Value : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = queue.TenantId.Value });
        cmd.Parameters.Add(new NpgsqlParameter("QueueId", NpgsqlDbType.Text) { Value = queue.QueueId.Value });
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task<object?> ColumnAsync(Queue queue)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT last_agent_routing_enabled FROM queue_configs WHERE tenant_id = @TenantId AND queue_id = @QueueId");
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = queue.TenantId.Value });
        cmd.Parameters.Add(new NpgsqlParameter("QueueId", NpgsqlDbType.Text) { Value = queue.QueueId.Value });
        return await cmd.ExecuteScalarAsync();
    }
}
