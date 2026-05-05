namespace Verbara.Platform.Core.Webhooks;

/// <summary>
/// JSON envelope sent to webhook endpoints via HTTP POST.
/// </summary>
public sealed record WebhookEventPayload(
    string Id,
    string Type,
    string TenantId,
    DateTimeOffset Timestamp,
    object Data);
