using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryAuditStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");

    private static AuditEntry MakeEntry(
        TenantId? tenantId = null,
        string action = "conversation.created",
        string targetType = "Conversation",
        string targetId = "conv-1",
        DateTimeOffset? occurredAt = null) => new()
    {
        EntryId = EntityId.New(),
        TenantId = tenantId ?? Tenant1,
        Action = action,
        TargetType = targetType,
        TargetId = targetId,
        OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task SaveAsync_ShouldPersistEntry()
    {
        var store = new InMemoryAuditStore();
        var entry = MakeEntry();

        await store.SaveAsync(entry, CancellationToken.None);

        var result = await store.GetByEntityAsync(Tenant1, entry.TargetType!, entry.TargetId!, CancellationToken.None);
        result.Should().HaveCount(1);
        result[0].EntryId.Should().Be(entry.EntryId);
    }

    [Fact]
    public async Task SaveAsync_ShouldRoundTripRetainUntil_WhenSet()
    {
        var store = new InMemoryAuditStore();
        var retainUntil = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "typification.autonomous.commit",
            TargetType = "Conversation",
            TargetId = "conv-retain",
            OccurredAt = DateTimeOffset.UtcNow,
            RetainUntil = retainUntil,
        };

        await store.SaveAsync(entry, CancellationToken.None);

        var result = await store.GetByEntityAsync(Tenant1, entry.TargetType!, entry.TargetId!, CancellationToken.None);
        result.Should().ContainSingle();
        result[0].RetainUntil.Should().Be(retainUntil);
    }

    [Fact]
    public async Task SaveAsync_ShouldLeaveRetainUntilNull_WhenNotSet()
    {
        var store = new InMemoryAuditStore();
        var entry = MakeEntry();

        await store.SaveAsync(entry, CancellationToken.None);

        var result = await store.GetByEntityAsync(Tenant1, entry.TargetType!, entry.TargetId!, CancellationToken.None);
        result.Should().ContainSingle();
        result[0].RetainUntil.Should().BeNull();
    }

    [Fact]
    public async Task GetByEntityAsync_ShouldReturnEmpty_WhenNoEntries()
    {
        var store = new InMemoryAuditStore();

        var result = await store.GetByEntityAsync(Tenant1, "Conversation", "conv-none", CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByEntityAsync_ShouldReturnOnlyMatchingEntity()
    {
        var store = new InMemoryAuditStore();
        var a = MakeEntry(targetId: "conv-A");
        var b = MakeEntry(targetId: "conv-B");
        await store.SaveAsync(a, CancellationToken.None);
        await store.SaveAsync(b, CancellationToken.None);

        var result = await store.GetByEntityAsync(Tenant1, "Conversation", "conv-A", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].TargetId.Should().Be("conv-A");
    }

    [Fact]
    public async Task GetByEntityAsync_ShouldReturnEntriesOrderedByOccurredAt()
    {
        var store = new InMemoryAuditStore();
        var base_ = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var second = MakeEntry(targetId: "conv-1", occurredAt: base_.AddMinutes(2));
        var first = MakeEntry(targetId: "conv-1", occurredAt: base_.AddMinutes(1));

        await store.SaveAsync(second, CancellationToken.None);
        await store.SaveAsync(first, CancellationToken.None);

        var result = await store.GetByEntityAsync(Tenant1, "Conversation", "conv-1", CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].OccurredAt.Should().BeBefore(result[1].OccurredAt);
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByAction()
    {
        var store = new InMemoryAuditStore();
        await store.SaveAsync(MakeEntry(action: "conversation.created"), CancellationToken.None);
        await store.SaveAsync(MakeEntry(action: "message.sent", targetType: "Message", targetId: "msg-1"), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuditQuery(Action: "message.sent"), CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be("message.sent");
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByDateRange()
    {
        var store = new InMemoryAuditStore();
        var base_ = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(MakeEntry(occurredAt: base_), CancellationToken.None);
        await store.SaveAsync(MakeEntry(targetId: "conv-2", occurredAt: base_.AddDays(10)), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuditQuery(From: base_.AddDays(5)), CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].TargetId.Should().Be("conv-2");
    }

    [Fact]
    public async Task SearchAsync_ShouldSupportPaging()
    {
        var store = new InMemoryAuditStore();
        for (var i = 1; i <= 5; i++)
            await store.SaveAsync(MakeEntry(targetId: $"conv-{i}"), CancellationToken.None);

        var page1 = await store.SearchAsync(Tenant1, new AuditQuery(Page: 1, PageSize: 2), CancellationToken.None);
        var page2 = await store.SearchAsync(Tenant1, new AuditQuery(Page: 2, PageSize: 2), CancellationToken.None);
        var page3 = await store.SearchAsync(Tenant1, new AuditQuery(Page: 3, PageSize: 2), CancellationToken.None);

        page1.Items.Should().HaveCount(2);
        page2.Items.Should().HaveCount(2);
        page3.Items.Should().HaveCount(1);
        page1.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task SearchAsync_ShouldIsolateTenants()
    {
        var store = new InMemoryAuditStore();
        await store.SaveAsync(MakeEntry(tenantId: Tenant1), CancellationToken.None);
        await store.SaveAsync(MakeEntry(tenantId: Tenant2), CancellationToken.None);

        var r1 = await store.SearchAsync(Tenant1, new AuditQuery(), CancellationToken.None);
        var r2 = await store.SearchAsync(Tenant2, new AuditQuery(), CancellationToken.None);

        r1.Items.Should().HaveCount(1);
        r2.Items.Should().HaveCount(1);
        r1.Items[0].TenantId.Should().Be(Tenant1);
        r2.Items[0].TenantId.Should().Be(Tenant2);
    }
}
