using Asterisk.Platform.Core.Notifications;
using Asterisk.Platform.Storage.InMemory;

namespace Asterisk.Platform.Api.Tests;

public sealed class NotificationStoreTests
{
    // ── helper ───────────────────────────────────────────────────────────────

    private static Notification CreateNotification(
        string id,
        string tenantId,
        string userId,
        NotificationSeverity severity = NotificationSeverity.Info,
        bool isRead = false) =>
        new()
        {
            NotificationId = id,
            TenantId = tenantId,
            UserId = userId,
            Category = NotificationCategory.Operational,
            Severity = severity,
            Type = "test.notification",
            Title = $"Title {id}",
            Body = $"Body {id}",
            IsRead = isRead,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ShouldPersistAndRetrieve()
    {
        var store = new InMemoryNotificationStore();
        var notification = CreateNotification("n1", "tenant-a", "user-1");

        await store.SaveAsync(notification);
        var fetched = await store.GetAsync("n1");

        fetched.Should().NotBeNull();
        fetched!.NotificationId.Should().Be("n1");
        fetched.Title.Should().Be("Title n1");
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByUserAndTenant()
    {
        var store = new InMemoryNotificationStore();

        await store.SaveAsync(CreateNotification("n1", "tenant-a", "user-1"));
        await store.SaveAsync(CreateNotification("n2", "tenant-a", "user-1"));
        await store.SaveAsync(CreateNotification("n3", "tenant-a", "user-2")); // different user
        await store.SaveAsync(CreateNotification("n4", "tenant-b", "user-1")); // different tenant

        var result = await store.ListAsync("tenant-a", "user-1", null, 100, 0);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(n =>
        {
            n.TenantId.Should().Be("tenant-a");
            n.UserId.Should().Be("user-1");
        });
    }

    [Fact]
    public async Task CountUnreadAsync_ShouldCountOnlyUnread()
    {
        var store = new InMemoryNotificationStore();

        await store.SaveAsync(CreateNotification("n1", "tenant-a", "user-1", isRead: false));
        await store.SaveAsync(CreateNotification("n2", "tenant-a", "user-1", isRead: true));
        await store.SaveAsync(CreateNotification("n3", "tenant-a", "user-1", isRead: false));
        await store.SaveAsync(CreateNotification("n4", "tenant-b", "user-1", isRead: false)); // different tenant

        var count = await store.CountUnreadAsync("tenant-a", "user-1");

        count.Should().Be(2);
    }

    [Fact]
    public async Task MarkReadAsync_ShouldSetIsReadAndReadAt()
    {
        var store = new InMemoryNotificationStore();
        var notification = CreateNotification("n1", "tenant-a", "user-1", isRead: false);
        await store.SaveAsync(notification);

        var before = DateTimeOffset.UtcNow;
        await store.MarkReadAsync("n1");
        var after = DateTimeOffset.UtcNow;

        var fetched = await store.GetAsync("n1");
        fetched!.IsRead.Should().BeTrue();
        fetched.ReadAt.Should().NotBeNull();
        fetched.ReadAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public async Task MarkAllReadAsync_ShouldMarkAllForUser()
    {
        var store = new InMemoryNotificationStore();

        await store.SaveAsync(CreateNotification("n1", "tenant-a", "user-1", isRead: false));
        await store.SaveAsync(CreateNotification("n2", "tenant-a", "user-1", isRead: false));
        await store.SaveAsync(CreateNotification("n3", "tenant-a", "user-2", isRead: false)); // other user

        await store.MarkAllReadAsync("tenant-a", "user-1");

        var n1 = await store.GetAsync("n1");
        var n2 = await store.GetAsync("n2");
        var n3 = await store.GetAsync("n3"); // should remain unread

        n1!.IsRead.Should().BeTrue();
        n2!.IsRead.Should().BeTrue();
        n3!.IsRead.Should().BeFalse();
    }
}
