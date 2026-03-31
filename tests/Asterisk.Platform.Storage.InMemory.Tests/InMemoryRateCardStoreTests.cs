using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public class InMemoryRateCardStoreTests
{
    private readonly InMemoryRateCardStore _store = new();
    private static readonly TenantId Tenant1 = new("t1");
    private static readonly TenantId Tenant2 = new("t2");

    private static RateCard MakeRateCard(TenantId tenantId, DateTimeOffset effectiveFrom, bool isDefault = false, DateTimeOffset? effectiveTo = null)
        => new()
        {
            RateCardId = EntityId.New(),
            TenantId = tenantId,
            Name = "Test Card",
            Currency = "USD",
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            Rates = new List<RateEntry>
            {
                new() { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m },
            },
            IsDefault = isDefault,
        };

    [Fact]
    public async Task SaveAsync_ShouldPersist_AndGetByIdAsync_ShouldRetrieve()
    {
        var card = MakeRateCard(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, card.RateCardId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.RateCardId.Should().Be(card.RateCardId);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var result = await _store.GetByIdAsync(Tenant1, EntityId.New(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldNotCrossTenants()
    {
        var card = MakeRateCard(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant2, card.RateCardId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnOnlyTenantCards()
    {
        await _store.SaveAsync(MakeRateCard(Tenant1, DateTimeOffset.UtcNow), CancellationToken.None);
        await _store.SaveAsync(MakeRateCard(Tenant1, DateTimeOffset.UtcNow), CancellationToken.None);
        await _store.SaveAsync(MakeRateCard(Tenant2, DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await _store.ListAsync(Tenant1, CancellationToken.None);
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveCard()
    {
        var card = MakeRateCard(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(card, CancellationToken.None);

        await _store.DeleteAsync(Tenant1, card.RateCardId, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, card.RateCardId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAsync_ShouldReturnCardEffectiveAtDate()
    {
        var asOf = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var card = MakeRateCard(Tenant1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetActiveAsync(Tenant1, asOf, CancellationToken.None);

        result.Should().NotBeNull();
        result!.RateCardId.Should().Be(card.RateCardId);
    }

    [Fact]
    public async Task GetActiveAsync_ShouldReturnNull_WhenNoActiveCard()
    {
        var asOf = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var card = MakeRateCard(Tenant1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetActiveAsync(Tenant1, asOf, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAsync_ShouldExcludeExpiredCards()
    {
        var asOf = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);
        var card = MakeRateCard(Tenant1,
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            effectiveTo: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetActiveAsync(Tenant1, asOf, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAsync_ShouldReturnMostRecentActive_WhenMultipleExist()
    {
        var asOf = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);
        var older = MakeRateCard(Tenant1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = MakeRateCard(Tenant1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(older, CancellationToken.None);
        await _store.SaveAsync(newer, CancellationToken.None);

        var result = await _store.GetActiveAsync(Tenant1, asOf, CancellationToken.None);

        result.Should().NotBeNull();
        result!.RateCardId.Should().Be(newer.RateCardId);
    }

    [Fact]
    public async Task SaveAsync_ShouldOverwriteExistingCard()
    {
        var card = MakeRateCard(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(card, CancellationToken.None);

        var updated = new RateCard
        {
            RateCardId = card.RateCardId,
            TenantId = Tenant1,
            Name = "Updated",
            Currency = "EUR",
            EffectiveFrom = card.EffectiveFrom,
            Rates = card.Rates,
        };
        await _store.SaveAsync(updated, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, card.RateCardId, CancellationToken.None);
        result!.Name.Should().Be("Updated");
        result.Currency.Should().Be("EUR");

        var list = await _store.ListAsync(Tenant1, CancellationToken.None);
        list.Should().HaveCount(1);
    }
}
