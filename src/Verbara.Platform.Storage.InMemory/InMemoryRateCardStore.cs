using System.Collections.Concurrent;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

public sealed class InMemoryRateCardStore : IRateCardStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), RateCard> _cards = new();

    public Task SaveAsync(RateCard rateCard, CancellationToken ct)
    {
        _cards[(rateCard.TenantId, rateCard.RateCardId)] = rateCard;
        return Task.CompletedTask;
    }

    public Task<RateCard?> GetByIdAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        _cards.TryGetValue((tenantId, rateCardId), out var card);
        return Task.FromResult(card);
    }

    public Task<RateCard?> GetActiveAsync(TenantId tenantId, DateTimeOffset asOf, CancellationToken ct)
    {
        var active = _cards.Values
            .Where(c => c.TenantId == tenantId
                && c.EffectiveFrom <= asOf
                && (c.EffectiveTo == null || c.EffectiveTo > asOf))
            .OrderByDescending(c => c.EffectiveFrom)
            .FirstOrDefault();

        return Task.FromResult(active);
    }

    public Task<IReadOnlyList<RateCard>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<RateCard> result = _cards.Values
            .Where(c => c.TenantId == tenantId)
            .ToList();

        return Task.FromResult(result);
    }

    public Task DeleteAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        _cards.TryRemove((tenantId, rateCardId), out _);
        return Task.CompletedTask;
    }
}
