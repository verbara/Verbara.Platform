using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// dialer-places-calls-end-to-end (design D11 point 3): <see cref="PostgresQueueStore.GetByNameAsync"/> against the
/// real embedded DDL. <c>queue_configs</c> is keyed by <c>(tenant_id, queue_id)</c> and names are not unique, so the
/// lookup is tenant-scoped, exact, and prefers the active queue. It must find a target that sorts past the first 500
/// queues (the page <c>ListAsync</c> would return). Each test uses unique tenant ids. Same three cases as
/// <c>InMemoryQueueStoreTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresQueueStoreByNameTests : IClassFixture<MigrationsFixture>
{
    private readonly PostgresQueueStore _store;

    public PostgresQueueStoreByNameTests(MigrationsFixture fixture)
        => _store = new PostgresQueueStore(fixture.DataSource);

    private static (TenantId Acme, TenantId Beta) Tenants()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return (new TenantId($"acme-{suffix}"), new TenantId($"beta-{suffix}"));
    }

    private static Queue MakeQueue(TenantId tenant, string id, string name, bool isActive = true) =>
        new()
        {
            QueueId = EntityId.From(id),
            TenantId = tenant,
            Name = name,
            IsActive = isActive,
            CreatedAt = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
        };

    [Fact]
    public async Task GetByNameAsync_ShouldReturnTheTenantsQueue_WhenTheNameMatches()
    {
        var (acme, beta) = Tenants();
        // 500 queues of acme whose names sort before the target, then the target: 501 in all.
        for (var i = 0; i < 500; i++)
            await _store.SaveAsync(MakeQueue(acme, $"q-acme-{i:D3}", $"a-{i:D3}"), CancellationToken.None);
        await _store.SaveAsync(MakeQueue(acme, "q-acme-sales", "sales"), CancellationToken.None);
        await _store.SaveAsync(MakeQueue(beta, "q-beta-sales", "sales"), CancellationToken.None);

        var found = await _store.GetByNameAsync(acme, "sales", CancellationToken.None);

        found.Should().NotBeNull("acme holds a queue named sales, past the first 500 by name");
        found!.TenantId.Should().Be(acme);
        found.QueueId.Should().Be(EntityId.From("q-acme-sales"));
    }

    [Fact]
    public async Task GetByNameAsync_ShouldPreferTheActiveQueue_WhenTwoShareTheName()
    {
        var (acme, _) = Tenants();
        // The inactive one has the lower id, so neither id order nor insertion order picks the active one.
        await _store.SaveAsync(MakeQueue(acme, "q-001", "sales", isActive: false), CancellationToken.None);
        await _store.SaveAsync(MakeQueue(acme, "q-002", "sales"), CancellationToken.None);

        var found = await _store.GetByNameAsync(acme, "sales", CancellationToken.None);

        found.Should().NotBeNull("acme holds two queues named sales");
        found!.QueueId.Should().Be(EntityId.From("q-002"), "the active queue wins over an inactive one of the same name");
        found.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetByNameAsync_ShouldReturnNull_WhenNoQueueHasTheName()
    {
        var (acme, beta) = Tenants();
        await _store.SaveAsync(MakeQueue(acme, "q-support", "support"), CancellationToken.None);
        await _store.SaveAsync(MakeQueue(beta, "q-beta-sales", "sales"), CancellationToken.None);

        var found = await _store.GetByNameAsync(acme, "sales", CancellationToken.None);

        found.Should().BeNull();
    }
}
