namespace Asterisk.Platform.Core.Webhooks;

public sealed record WebhookSubscription(
    string SubscriptionId,
    string TenantId,
    string Name,
    string EndpointUrl,
    string Secret,
    IReadOnlyList<string> EventTypes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
