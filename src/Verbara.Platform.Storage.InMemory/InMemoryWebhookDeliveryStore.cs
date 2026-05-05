using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryWebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly ConcurrentDictionary<string, WebhookDelivery> _deliveries = new();

    public Task SaveAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        _deliveries[delivery.DeliveryId] = delivery;
        return Task.CompletedTask;
    }

    public Task<WebhookDelivery?> GetByIdAsync(string deliveryId, CancellationToken ct)
    {
        _deliveries.TryGetValue(deliveryId, out var delivery);
        return Task.FromResult(delivery);
    }

    public Task<IReadOnlyList<WebhookDelivery>> ListPendingRetriesAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        IReadOnlyList<WebhookDelivery> result = _deliveries.Values
            .Where(d => d.Status == WebhookDeliveryStatus.Pending
                && d.NextRetryAt.HasValue
                && d.NextRetryAt.Value <= now)
            .OrderBy(d => d.NextRetryAt)
            .Take(batchSize)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<PagedResult<WebhookDelivery>> ListBySubscriptionAsync(
        string subscriptionId, int page, int pageSize, CancellationToken ct)
    {
        var filtered = _deliveries.Values
            .Where(d => string.Equals(d.SubscriptionId, subscriptionId, StringComparison.Ordinal))
            .OrderByDescending(d => d.CreatedAt)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<WebhookDelivery>(items, totalCount, page, pageSize));
    }

    public Task<PagedResult<WebhookDelivery>> ListDeadLetterAsync(
        string tenantId, int page, int pageSize, CancellationToken ct)
    {
        var filtered = _deliveries.Values
            .Where(d => d.Status == WebhookDeliveryStatus.DeadLetter
                && string.Equals(d.TenantId, tenantId, StringComparison.Ordinal))
            .OrderByDescending(d => d.CreatedAt)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<WebhookDelivery>(items, totalCount, page, pageSize));
    }

    public Task UpdateAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        _deliveries[delivery.DeliveryId] = delivery;
        return Task.CompletedTask;
    }

    public Task DeleteBySubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        var toRemove = _deliveries.Values
            .Where(d => string.Equals(d.SubscriptionId, subscriptionId, StringComparison.Ordinal))
            .Select(d => d.DeliveryId)
            .ToList();

        foreach (var id in toRemove)
            _deliveries.TryRemove(id, out _);

        return Task.CompletedTask;
    }
}
