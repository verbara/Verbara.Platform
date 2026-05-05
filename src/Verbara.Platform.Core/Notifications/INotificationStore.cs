namespace Verbara.Platform.Core.Notifications;

public interface INotificationStore
{
    ValueTask<Notification?> GetAsync(string notificationId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Notification>> ListAsync(string tenantId, string userId,
        bool? unreadOnly, int limit, int offset, CancellationToken ct = default);
    ValueTask<int> CountUnreadAsync(string tenantId, string userId, CancellationToken ct = default);
    ValueTask SaveAsync(Notification notification, CancellationToken ct = default);
    ValueTask MarkReadAsync(string notificationId, CancellationToken ct = default);
    ValueTask MarkAllReadAsync(string tenantId, string userId, CancellationToken ct = default);
}
