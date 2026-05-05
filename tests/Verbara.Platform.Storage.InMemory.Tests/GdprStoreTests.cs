using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class GdprStoreTests
{
    private const string Tenant1 = "tenant-1";
    private const string Tenant2 = "tenant-2";

    // ─── PurgeLogStore ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeLogStore_SaveAsync_ShouldPersistEntry()
    {
        var store = new InMemoryPurgeLogStore();
        var entry = MakePurgeEntry();

        await store.SaveAsync(entry, CancellationToken.None);

        var result = await store.ListAsync(Tenant1, null, null, 1, 10, CancellationToken.None);
        result.Items.Should().HaveCount(1);
        result.Items[0].PurgeId.Should().Be(entry.PurgeId);
    }

    [Fact]
    public async Task PurgeLogStore_ListAsync_ShouldFilterByTenant()
    {
        var store = new InMemoryPurgeLogStore();
        await store.SaveAsync(MakePurgeEntry(Tenant1), CancellationToken.None);
        await store.SaveAsync(MakePurgeEntry(Tenant2), CancellationToken.None);

        var result = await store.ListAsync(Tenant1, null, null, 1, 10, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].TenantId.Should().Be(Tenant1);
    }

    [Fact]
    public async Task PurgeLogStore_ListAsync_ShouldFilterByDateRange()
    {
        var store = new InMemoryPurgeLogStore();
        var base_ = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(MakePurgeEntry(purgedAt: base_), CancellationToken.None);
        await store.SaveAsync(MakePurgeEntry(purgedAt: base_.AddDays(10)), CancellationToken.None);

        var result = await store.ListAsync(null, base_.AddDays(5), null, 1, 10, CancellationToken.None);

        result.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task PurgeLogStore_ListAsync_ShouldSupportPaging()
    {
        var store = new InMemoryPurgeLogStore();
        for (var i = 0; i < 5; i++)
            await store.SaveAsync(MakePurgeEntry(), CancellationToken.None);

        var page1 = await store.ListAsync(null, null, null, 1, 2, CancellationToken.None);
        var page2 = await store.ListAsync(null, null, null, 2, 2, CancellationToken.None);

        page1.Items.Should().HaveCount(2);
        page2.Items.Should().HaveCount(2);
        page1.TotalCount.Should().Be(5);
    }

    // ─── RetentionPolicyStore ───────────────────────────────────────────────────

    [Fact]
    public async Task RetentionPolicyStore_GetAsync_ShouldReturnNull_WhenNotSet()
    {
        var store = new InMemoryTenantRetentionPolicyStore();

        var result = await store.GetAsync(Tenant1, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task RetentionPolicyStore_SaveAsync_ShouldPersistAndRetrieve()
    {
        var store = new InMemoryTenantRetentionPolicyStore();
        var policy = new TenantRetentionPolicy
        {
            TenantId = Tenant1,
            ConversationRetentionDays = 90,
            AuthEventRetentionDays = 365,
        };

        await store.SaveAsync(policy, CancellationToken.None);
        var result = await store.GetAsync(Tenant1, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ConversationRetentionDays.Should().Be(90);
        result.AuthEventRetentionDays.Should().Be(365);
        result.AuditRetentionDays.Should().BeNull();
    }

    [Fact]
    public async Task RetentionPolicyStore_SaveAsync_ShouldOverwriteExisting()
    {
        var store = new InMemoryTenantRetentionPolicyStore();
        await store.SaveAsync(new TenantRetentionPolicy { TenantId = Tenant1, ConversationRetentionDays = 30 }, CancellationToken.None);
        await store.SaveAsync(new TenantRetentionPolicy { TenantId = Tenant1, ConversationRetentionDays = 60 }, CancellationToken.None);

        var result = await store.GetAsync(Tenant1, CancellationToken.None);

        result!.ConversationRetentionDays.Should().Be(60);
    }

    [Fact]
    public async Task RetentionPolicyStore_ListActiveAsync_ShouldReturnOnlyNonNullPolicies()
    {
        var store = new InMemoryTenantRetentionPolicyStore();
        await store.SaveAsync(new TenantRetentionPolicy { TenantId = Tenant1, ConversationRetentionDays = 30 }, CancellationToken.None);
        await store.SaveAsync(new TenantRetentionPolicy { TenantId = Tenant2 }, CancellationToken.None); // all null

        var result = await store.ListActiveAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].TenantId.Should().Be(Tenant1);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static PurgeEntry MakePurgeEntry(string? tenantId = null, DateTimeOffset? purgedAt = null) => new()
    {
        PurgeId = Guid.NewGuid().ToString("N"),
        TenantId = tenantId ?? Tenant1,
        SubjectType = "contact",
        SubjectId = "contact-1",
        PerformedBy = "admin-user",
        Reason = "GDPR erasure request",
        EntitiesDeleted = new Dictionary<string, int> { ["messages"] = 10, ["conversations"] = 2, ["contact"] = 1 },
        PurgedAt = purgedAt ?? DateTimeOffset.UtcNow,
    };
}
