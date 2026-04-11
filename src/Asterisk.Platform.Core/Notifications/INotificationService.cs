namespace Asterisk.Platform.Core.Notifications;

/// <summary>
/// Orchestrates creation of in-app notifications for a tenant. Resolves target users
/// by role (via <see cref="NotificationTypeRegistry"/>), persists notifications,
/// publishes SSE events, sends critical emails, and propagates to parent tenants.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Create a notification of the given type for the tenant. The registered
    /// <see cref="NotificationTypeInfo"/> determines category, severity, and which
    /// user roles receive the notification.
    /// </summary>
    Task CreateAsync(
        string tenantId,
        string type,
        string title,
        string body,
        string? actionUrl = null,
        CancellationToken ct = default);
}
