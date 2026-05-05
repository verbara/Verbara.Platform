namespace Verbara.Platform.Core.Webhooks;

public interface IWebhookSubscriptionStore
{
    Task<WebhookSubscription?> GetByIdAsync(string subscriptionId, CancellationToken ct);
    Task<IReadOnlyList<WebhookSubscription>> ListByTenantAsync(string tenantId, CancellationToken ct);
    Task<IReadOnlyList<WebhookSubscription>> GetActiveByEventTypeAsync(
        string tenantId, string eventType, CancellationToken ct);
    Task SaveAsync(WebhookSubscription subscription, CancellationToken ct);
    Task DeleteAsync(string subscriptionId, CancellationToken ct);
}
