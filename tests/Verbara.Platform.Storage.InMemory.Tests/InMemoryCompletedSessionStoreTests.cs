using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryCompletedSessionStoreTests
{
    private const string Tenant = "tenant-1";

    private static CompletedSessionRow MakeRow(string tenantId, string sessionId) =>
        new()
        {
            TenantId = tenantId,
            SessionId = sessionId,
            ServerId = "server-1",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldReturnRequestedSubset_WhenMultipleUpserted()
    {
        var store = new InMemoryCompletedSessionStore();
        await store.UpsertAsync(MakeRow(Tenant, "s1"), CancellationToken.None);
        await store.UpsertAsync(MakeRow(Tenant, "s2"), CancellationToken.None);
        await store.UpsertAsync(MakeRow(Tenant, "s3"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync(Tenant, ["s1", "s3"], CancellationToken.None);

        result.Select(r => r.SessionId).Should().BeEquivalentTo("s1", "s3");
    }

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldIsolateByTenant_WhenSameIdAcrossTenants()
    {
        var store = new InMemoryCompletedSessionStore();
        await store.UpsertAsync(MakeRow("tenant-a", "shared"), CancellationToken.None);
        await store.UpsertAsync(MakeRow("tenant-b", "shared"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync("tenant-a", ["shared"], CancellationToken.None);

        result.Should().ContainSingle().Which.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldReturnEmpty_WhenEmptyCollection()
    {
        var store = new InMemoryCompletedSessionStore();
        await store.UpsertAsync(MakeRow(Tenant, "s1"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync(Tenant, [], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldOmitUnknownIds_WhenSomeNotPresent()
    {
        var store = new InMemoryCompletedSessionStore();
        await store.UpsertAsync(MakeRow(Tenant, "known"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync(Tenant, ["known", "missing"], CancellationToken.None);

        result.Should().ContainSingle().Which.SessionId.Should().Be("known");
    }

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldReturnEachRowOnce_WhenDuplicateIdsRequested()
    {
        var store = new InMemoryCompletedSessionStore();
        await store.UpsertAsync(MakeRow(Tenant, "dup"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync(Tenant, ["dup", "dup"], CancellationToken.None);

        result.Should().ContainSingle().Which.SessionId.Should().Be("dup");
    }
}
