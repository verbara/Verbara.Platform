using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Phase 2.1 — coverage for the <see cref="IDidRouteStore"/> contract against the
/// in-memory backend. The duplicate-DID conflict and tenant-isolation guarantees
/// are asserted here because they MUST behave identically to the Postgres
/// <c>UNIQUE (tenant_id, did)</c> constraint.
/// </summary>
public sealed class DidRouteStoreTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private static InMemoryDidRouteStore NewStore() => new();

    private static DidRoute NewRoute(string tenantId, string did, string queueId, bool isActive = true) => new()
    {
        RouteId = EntityId.New(),
        TenantId = new TenantId(tenantId),
        Did = did,
        QueueId = EntityId.From(queueId),
        IsActive = isActive,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task CreateAsync_ShouldReturnPersistedRoute_WhenDidIsUnique()
    {
        var store = NewStore();

        var created = await store.CreateAsync(NewRoute(TenantA, "+15550001", "queue-1"), CancellationToken.None);

        created.Did.Should().Be("+15550001");
        created.QueueId.Value.Should().Be("queue-1");
        created.RouteId.Value.Should().NotBeNullOrWhiteSpace();
        created.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnRoute_WhenRouteIdExists()
    {
        var store = NewStore();
        var created = await store.CreateAsync(NewRoute(TenantA, "+15550002", "queue-2"), CancellationToken.None);

        var fetched = await store.GetAsync(new TenantId(TenantA), created.RouteId, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.RouteId.Should().Be(created.RouteId);
        fetched.Did.Should().Be("+15550002");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenRouteIdMissing()
    {
        var store = NewStore();

        var fetched = await store.GetAsync(new TenantId(TenantA), EntityId.From("nonexistent"), CancellationToken.None);

        fetched.Should().BeNull();
    }

    [Fact]
    public async Task GetByDidAsync_ShouldReturnRoute_WhenDidExists()
    {
        var store = NewStore();
        await store.CreateAsync(NewRoute(TenantA, "+15550003", "queue-3"), CancellationToken.None);

        var fetched = await store.GetByDidAsync(new TenantId(TenantA), "+15550003", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.QueueId.Value.Should().Be("queue-3");
    }

    [Fact]
    public async Task GetByDidAsync_ShouldReturnNull_WhenDidMissing()
    {
        var store = NewStore();

        var fetched = await store.GetByDidAsync(new TenantId(TenantA), "+10000000", CancellationToken.None);

        fetched.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnAllRoutesForTenant_OrderedByDid()
    {
        var store = NewStore();
        await store.CreateAsync(NewRoute(TenantA, "+15550030", "queue-z"), CancellationToken.None);
        await store.CreateAsync(NewRoute(TenantA, "+15550010", "queue-a"), CancellationToken.None);
        await store.CreateAsync(NewRoute(TenantA, "+15550020", "queue-m", isActive: false), CancellationToken.None);

        var all = await store.ListAsync(new TenantId(TenantA), CancellationToken.None);

        all.Should().HaveCount(3);
        all.Select(r => r.Did).Should().ContainInOrder("+15550010", "+15550020", "+15550030");
    }

    [Fact]
    public async Task ListActiveAsync_ShouldExcludeInactiveRoutes_WhenSomeAreDisabled()
    {
        var store = NewStore();
        await store.CreateAsync(NewRoute(TenantA, "+15550040", "queue-1"), CancellationToken.None);
        await store.CreateAsync(NewRoute(TenantA, "+15550041", "queue-2", isActive: false), CancellationToken.None);

        var active = await store.ListActiveAsync(new TenantId(TenantA), CancellationToken.None);

        active.Should().HaveCount(1);
        active[0].Did.Should().Be("+15550040");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowInvalidOperation_WhenDidDuplicatedWithinTenant()
    {
        var store = NewStore();
        await store.CreateAsync(NewRoute(TenantA, "+15559999", "queue-1"), CancellationToken.None);

        var act = async () => await store.CreateAsync(
            NewRoute(TenantA, "+15559999", "queue-2"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldSucceed_WhenSameDidUsedAcrossDifferentTenants()
    {
        var store = NewStore();
        await store.CreateAsync(NewRoute(TenantA, "+15558888", "queue-1"), CancellationToken.None);

        var act = async () => await store.CreateAsync(
            NewRoute(TenantB, "+15558888", "queue-2"), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateAsync_ShouldPersistChanges_WhenRouteExists()
    {
        var store = NewStore();
        var created = await store.CreateAsync(NewRoute(TenantA, "+15557777", "queue-1"), CancellationToken.None);

        created.QueueId = EntityId.From("queue-updated");
        created.IsActive = false;
        await store.UpdateAsync(created, CancellationToken.None);

        var fetched = await store.GetAsync(new TenantId(TenantA), created.RouteId, CancellationToken.None);
        fetched!.QueueId.Value.Should().Be("queue-updated");
        fetched.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrowInvalidOperation_WhenRenamingOntoExistingDidInTenant()
    {
        // Parity with Postgres UNIQUE (tenant_id, did): renaming one route's DID onto
        // another route's DID in the same tenant must conflict on BOTH backends.
        var store = NewStore();
        await store.CreateAsync(NewRoute(TenantA, "+15550100", "queue-1"), CancellationToken.None);
        var second = await store.CreateAsync(NewRoute(TenantA, "+15550200", "queue-2"), CancellationToken.None);

        second.Did = "+15550100";
        var act = async () => await store.UpdateAsync(second, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_ShouldBeNoOp_WhenRouteDoesNotExist()
    {
        // Parity with Postgres `UPDATE ... WHERE` (0 rows affected) — never a phantom insert.
        var store = NewStore();
        var ghost = NewRoute(TenantA, "+15550300", "queue-1");

        await store.UpdateAsync(ghost, CancellationToken.None);

        var fetched = await store.GetAsync(new TenantId(TenantA), ghost.RouteId, CancellationToken.None);
        fetched.Should().BeNull();
        (await store.ListAsync(new TenantId(TenantA), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ShouldBeNoOp_WhenRouteBelongsToAnotherTenant()
    {
        // A cross-tenant (tenant, route_id) pair is not matched by the Postgres WHERE
        // clause, so InMemory must also leave the real owner's row untouched.
        var store = NewStore();
        var owned = await store.CreateAsync(NewRoute(TenantA, "+15550400", "queue-a"), CancellationToken.None);

        var spoof = new DidRoute
        {
            RouteId = owned.RouteId,
            TenantId = new TenantId(TenantB),
            Did = "+15550400",
            QueueId = EntityId.From("queue-evil"),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.UpdateAsync(spoof, CancellationToken.None);

        (await store.GetAsync(new TenantId(TenantB), owned.RouteId, CancellationToken.None)).Should().BeNull();
        var stillOwned = await store.GetAsync(new TenantId(TenantA), owned.RouteId, CancellationToken.None);
        stillOwned!.QueueId.Value.Should().Be("queue-a");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRoute_WhenRouteExists()
    {
        var store = NewStore();
        var created = await store.CreateAsync(NewRoute(TenantA, "+15556666", "queue-1"), CancellationToken.None);

        await store.DeleteAsync(new TenantId(TenantA), created.RouteId, CancellationToken.None);

        var fetched = await store.GetAsync(new TenantId(TenantA), created.RouteId, CancellationToken.None);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldIsolateTenants_WhenMultipleTenantsHaveRoutes()
    {
        var store = NewStore();
        await store.CreateAsync(NewRoute(TenantA, "+15551111", "queue-a"), CancellationToken.None);
        await store.CreateAsync(NewRoute(TenantB, "+15552222", "queue-b"), CancellationToken.None);

        var tenantARoutes = await store.ListAsync(new TenantId(TenantA), CancellationToken.None);
        var tenantBRoutes = await store.ListAsync(new TenantId(TenantB), CancellationToken.None);

        tenantARoutes.Should().ContainSingle().Which.Did.Should().Be("+15551111");
        tenantBRoutes.Should().ContainSingle().Which.Did.Should().Be("+15552222");
    }

    [Fact]
    public async Task GetAsync_ShouldNotLeakAcrossTenants_WhenRouteBelongsToAnotherTenant()
    {
        var store = NewStore();
        var created = await store.CreateAsync(NewRoute(TenantA, "+15553333", "queue-a"), CancellationToken.None);

        var fetched = await store.GetAsync(new TenantId(TenantB), created.RouteId, CancellationToken.None);

        fetched.Should().BeNull();
    }
}
