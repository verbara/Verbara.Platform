namespace Asterisk.Platform.Core.Webhooks;

public enum CircuitStatus { Closed, Open, HalfOpen }

public sealed record WebhookSubscription(
    string SubscriptionId,
    string TenantId,
    string Name,
    string EndpointUrl,
    string Secret,
    IReadOnlyList<string> EventTypes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CircuitStatus CircuitStatus = CircuitStatus.Closed,
    int CircuitFailures = 0,
    DateTimeOffset? CircuitOpenedAt = null,
    DateTimeOffset? CircuitNextProbeAt = null,
    int CircuitProbeAttempts = 0);
