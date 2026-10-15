using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryWebhookDeliveryStore : IWebhookDeliveryStore
{
    // One lock serialises every read and write, so a claim's check and its write are one step, as the
    // claim statement's row lock makes them in Postgres (webhook-delivery-runs-once, design D8). The claim is
    // kept beside the record, never inside it: it is not part of the delivery API.
    private readonly Dictionary<string, Entry> _deliveries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public Task SaveAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        lock (_lock)
            _deliveries[delivery.DeliveryId] = new Entry(delivery);
        return Task.CompletedTask;
    }

    public Task<WebhookDelivery?> GetByIdAsync(string deliveryId, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult(_deliveries.TryGetValue(deliveryId, out var entry) ? entry.Delivery : null);
    }

    public Task<IReadOnlyList<WebhookDelivery>> ListPendingRetriesAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<WebhookDelivery> result = _deliveries.Values
                .Select(e => e.Delivery)
                .Where(d => d.Status == WebhookDeliveryStatus.Pending
                    && d.NextRetryAt.HasValue
                    && d.NextRetryAt.Value <= now)
                .OrderBy(d => d.NextRetryAt)
                .Take(batchSize)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<PagedResult<WebhookDelivery>> ListBySubscriptionAsync(
        string subscriptionId, int page, int pageSize, CancellationToken ct)
    {
        lock (_lock)
        {
            var filtered = _deliveries.Values
                .Select(e => e.Delivery)
                .Where(d => string.Equals(d.SubscriptionId, subscriptionId, StringComparison.Ordinal))
                .OrderByDescending(d => d.CreatedAt)
                .ToList();

            var totalCount = filtered.Count;
            var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new PagedResult<WebhookDelivery>(items, totalCount, page, pageSize));
        }
    }

    public Task<PagedResult<WebhookDelivery>> ListDeadLetterAsync(
        string tenantId, int page, int pageSize, CancellationToken ct)
    {
        lock (_lock)
        {
            var filtered = _deliveries.Values
                .Select(e => e.Delivery)
                .Where(d => d.Status == WebhookDeliveryStatus.DeadLetter
                    && string.Equals(d.TenantId, tenantId, StringComparison.Ordinal))
                .OrderByDescending(d => d.CreatedAt)
                .ToList();

            var totalCount = filtered.Count;
            var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new PagedResult<WebhookDelivery>(items, totalCount, page, pageSize));
        }
    }

    public Task UpdateAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        lock (_lock)
        {
            // Postgres' UPDATE leaves the claim columns alone; so does this.
            _deliveries[delivery.DeliveryId] = _deliveries.TryGetValue(delivery.DeliveryId, out var entry)
                ? entry with { Delivery = delivery }
                : new Entry(delivery);
        }
        return Task.CompletedTask;
    }

    public Task DeleteBySubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        lock (_lock)
        {
            var toRemove = _deliveries.Values
                .Where(e => string.Equals(e.Delivery.SubscriptionId, subscriptionId, StringComparison.Ordinal))
                .Select(e => e.Delivery.DeliveryId)
                .ToList();

            foreach (var id in toRemove)
                _deliveries.Remove(id);
        }

        return Task.CompletedTask;
    }

    public Task<WebhookDeliveryClaim?> TryClaimAsync(
        string deliveryId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        lock (_lock)
        {
            var claim = _deliveries.TryGetValue(deliveryId, out var entry) && IsClaimable(entry, now)
                ? Claim(entry, expiresAt)
                : null;
            return Task.FromResult(claim);
        }
    }

    public Task<WebhookDeliveryClaim?> ClaimNextDueAsync(
        DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        lock (_lock)
        {
            var next = _deliveries.Values
                .Where(e => IsClaimable(e, now))
                .OrderBy(e => e.Delivery.NextRetryAt)
                .FirstOrDefault();
            return Task.FromResult(next is null ? null : Claim(next, expiresAt));
        }
    }

    public Task<bool> CompleteAttemptAsync(WebhookDelivery outcome, string claimId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!HoldsClaim(outcome.DeliveryId, claimId, out var entry))
                return Task.FromResult(false);

            // The columns Postgres' fenced UPDATE writes; the rest of the row is the stored one.
            var recorded = entry.Delivery with
            {
                Status = outcome.Status,
                Attempts = outcome.Attempts,
                NextRetryAt = outcome.NextRetryAt,
                LastResponseCode = outcome.LastResponseCode,
                LastError = outcome.LastError,
                DeliveredAt = outcome.DeliveredAt,
            };
            _deliveries[outcome.DeliveryId] = new Entry(recorded);
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseClaimAsync(
        string deliveryId, string claimId, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!HoldsClaim(deliveryId, claimId, out var entry))
                return Task.FromResult(false);

            _deliveries[deliveryId] = new Entry(entry.Delivery with { NextRetryAt = nextRetryAt });
            return Task.FromResult(true);
        }
    }

    // The Postgres claim predicate: Pending, due, and no claim whose lease is still running.
    private static bool IsClaimable(Entry entry, DateTimeOffset now) =>
        entry.Delivery.Status == WebhookDeliveryStatus.Pending
        && entry.Delivery.NextRetryAt is { } due && due <= now
        && (entry.ClaimExpiresAt is not { } expires || expires <= now);

    // Callers hold _lock.
    private WebhookDeliveryClaim Claim(Entry entry, DateTimeOffset expiresAt)
    {
        var claimId = Guid.NewGuid().ToString("N");
        _deliveries[entry.Delivery.DeliveryId] = entry with { ClaimId = claimId, ClaimExpiresAt = expiresAt };
        return new WebhookDeliveryClaim(entry.Delivery, claimId, Reclaimed: entry.ClaimId is not null);
    }

    // Callers hold _lock. The fence: the row's current claim is claimId.
    private bool HoldsClaim(string deliveryId, string claimId, [NotNullWhen(true)] out Entry? entry) =>
        _deliveries.TryGetValue(deliveryId, out entry)
        && string.Equals(entry.ClaimId, claimId, StringComparison.Ordinal);

    private sealed record Entry(WebhookDelivery Delivery, string? ClaimId = null, DateTimeOffset? ClaimExpiresAt = null);
}
