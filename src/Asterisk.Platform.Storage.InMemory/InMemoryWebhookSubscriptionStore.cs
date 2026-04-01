using System.Collections.Concurrent;
using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryWebhookSubscriptionStore : IWebhookSubscriptionStore
{
    private readonly ConcurrentDictionary<string, WebhookSubscription> _subscriptions = new();

    public Task<WebhookSubscription?> GetByIdAsync(string subscriptionId, CancellationToken ct)
    {
        _subscriptions.TryGetValue(subscriptionId, out var sub);
        return Task.FromResult(sub);
    }

    public Task<IReadOnlyList<WebhookSubscription>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        IReadOnlyList<WebhookSubscription> result = _subscriptions.Values
            .Where(s => string.Equals(s.TenantId, tenantId, StringComparison.Ordinal))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<WebhookSubscription>> GetActiveByEventTypeAsync(
        string tenantId, string eventType, CancellationToken ct)
    {
        IReadOnlyList<WebhookSubscription> result = _subscriptions.Values
            .Where(s => s.IsActive
                && string.Equals(s.TenantId, tenantId, StringComparison.Ordinal)
                && s.EventTypes.Contains(eventType))
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        _subscriptions[subscription.SubscriptionId] = subscription;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string subscriptionId, CancellationToken ct)
    {
        _subscriptions.TryRemove(subscriptionId, out _);
        return Task.CompletedTask;
    }
}
