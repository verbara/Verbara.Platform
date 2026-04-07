using System.Collections.Concurrent;
using Asterisk.Platform.Core.Notifications;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryNotificationStore : INotificationStore
{
    private readonly ConcurrentDictionary<string, Notification> _store = new();

    public ValueTask<Notification?> GetAsync(string notificationId, CancellationToken ct = default)
    {
        _store.TryGetValue(notificationId, out var notification);
        return ValueTask.FromResult(notification);
    }

    public ValueTask<IReadOnlyList<Notification>> ListAsync(
        string tenantId,
        string userId,
        bool? unreadOnly,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        var query = _store.Values
            .Where(n => string.Equals(n.TenantId, tenantId, StringComparison.Ordinal)
                     && string.Equals(n.UserId, userId, StringComparison.Ordinal));

        if (unreadOnly == true)
            query = query.Where(n => !n.IsRead);

        IReadOnlyList<Notification> result = query
            .OrderByDescending(n => n.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return ValueTask.FromResult(result);
    }

    public ValueTask<int> CountUnreadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        var count = _store.Values.Count(n =>
            string.Equals(n.TenantId, tenantId, StringComparison.Ordinal)
            && string.Equals(n.UserId, userId, StringComparison.Ordinal)
            && !n.IsRead);

        return ValueTask.FromResult(count);
    }

    public ValueTask SaveAsync(Notification notification, CancellationToken ct = default)
    {
        _store[notification.NotificationId] = notification;
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkReadAsync(string notificationId, CancellationToken ct = default)
    {
        if (_store.TryGetValue(notificationId, out var notification))
        {
            notification.IsRead = true;
            notification.ReadAt = DateTimeOffset.UtcNow;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask MarkAllReadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var notification in _store.Values
                     .Where(n => string.Equals(n.TenantId, tenantId, StringComparison.Ordinal)
                              && string.Equals(n.UserId, userId, StringComparison.Ordinal)
                              && !n.IsRead))
        {
            notification.IsRead = true;
            notification.ReadAt = now;
        }

        return ValueTask.CompletedTask;
    }
}
