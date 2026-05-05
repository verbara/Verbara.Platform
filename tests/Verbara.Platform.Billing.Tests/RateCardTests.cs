using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Billing.Tests;

public class RateCardTests
{
    [Fact]
    public void RateCard_ShouldExposeAllProperties_WhenConstructed()
    {
        var id = EntityId.New();
        var tenantId = new TenantId("t1");
        var effectiveFrom = DateTimeOffset.UtcNow;
        var rates = new List<RateEntry>
        {
            new() { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m },
        };

        var card = new RateCard
        {
            RateCardId = id,
            TenantId = tenantId,
            Name = "Standard",
            Currency = "USD",
            EffectiveFrom = effectiveFrom,
            Rates = rates,
            IsDefault = true,
        };

        card.RateCardId.Should().Be(id);
        card.TenantId.Should().Be(tenantId);
        card.Name.Should().Be("Standard");
        card.Currency.Should().Be("USD");
        card.EffectiveFrom.Should().Be(effectiveFrom);
        card.EffectiveTo.Should().BeNull();
        card.Rates.Should().HaveCount(1);
        card.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void RateCard_ShouldImplementITenantScoped()
    {
#pragma warning disable CA1859
        ITenantScoped scoped = new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = new TenantId("t1"),
            Name = "Test",
            Currency = "USD",
            EffectiveFrom = DateTimeOffset.UtcNow,
            Rates = [],
        };
#pragma warning restore CA1859

        scoped.TenantId.Should().Be(new TenantId("t1"));
    }

    [Fact]
    public void RateEntry_ShouldSupportTieredPricing()
    {
        var entry = new RateEntry
        {
            UsageType = UsageType.VoiceInbound,
            UnitPrice = 0.10m,
            IncludedQuantity = 100m,
            Tiers = new List<RateTier>
            {
                new() { FromQuantity = 0m, ToQuantity = 100m, UnitPrice = 0.10m },
                new() { FromQuantity = 100m, ToQuantity = 500m, UnitPrice = 0.08m },
                new() { FromQuantity = 500m, ToQuantity = null, UnitPrice = 0.05m },
            },
        };

        entry.Tiers.Should().HaveCount(3);
        entry.Tiers![2].ToQuantity.Should().BeNull();
    }

    [Fact]
    public void RateEntry_ShouldDefaultIncludedQuantityToZero()
    {
        var entry = new RateEntry
        {
            UsageType = UsageType.SmsOutbound,
            UnitPrice = 0.02m,
        };

        entry.IncludedQuantity.Should().Be(0m);
        entry.Tiers.Should().BeNull();
    }
}
