namespace Asterisk.Platform.Core.Webhooks;

public interface IWebhookDeliveryStore
{
    Task SaveAsync(WebhookDelivery delivery, CancellationToken ct);
    Task<WebhookDelivery?> GetByIdAsync(string deliveryId, CancellationToken ct);
    Task<IReadOnlyList<WebhookDelivery>> ListPendingRetriesAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct);
    Task<PagedResult<WebhookDelivery>> ListBySubscriptionAsync(
        string subscriptionId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<WebhookDelivery>> ListDeadLetterAsync(
        string tenantId, int page, int pageSize, CancellationToken ct);
    Task UpdateAsync(WebhookDelivery delivery, CancellationToken ct);
    Task DeleteBySubscriptionAsync(string subscriptionId, CancellationToken ct);
}
