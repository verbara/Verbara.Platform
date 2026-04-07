namespace Asterisk.Platform.Core.Notifications;

public sealed class Notification
{
    public required string NotificationId { get; init; }
    public required string TenantId { get; init; }
    public required string? UserId { get; init; }
    public required NotificationCategory Category { get; init; }
    public required NotificationSeverity Severity { get; init; }
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string? ActionUrl { get; init; }
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

public enum NotificationCategory { Operational = 0, System = 1, Security = 2, Billing = 3 }

public enum NotificationSeverity { Info = 0, Warning = 1, Critical = 2 }
