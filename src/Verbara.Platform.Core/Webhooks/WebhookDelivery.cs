namespace Verbara.Platform.Core.Webhooks;

public sealed record WebhookDelivery(
    string DeliveryId,
    string TenantId,
    string SubscriptionId,
    string EventType,
    string Payload,
    WebhookDeliveryStatus Status,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset? NextRetryAt,
    int? LastResponseCode,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);

public enum WebhookDeliveryStatus
{
    Pending,
    Delivered,
    Failed,
    DeadLetter
}
