using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class DefaultInvoiceGenerationServiceTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset PeriodStart = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

    private static (DefaultInvoiceGenerationService Service, IRateCardStore RateCardStore, IUsageRecordStore UsageStore, IClock Clock) Build()
    {
        var rateCardStore = Substitute.For<IRateCardStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultInvoiceGenerationService(rateCardStore, usageStore, clock);
        return (service, rateCardStore, usageStore, clock);
    }

    private static RateCard MakeFlatRateCard(params RateEntry[] rates) => new()
    {
        RateCardId = EntityId.New(),
        TenantId = Tenant1,
        Name = "Standard",
        Currency = "USD",
        EffectiveFrom = PeriodStart.AddMonths(-1),
        Rates = rates.ToList(),
        IsDefault = true,
    };

    [Fact]
    public async Task GenerateAsync_ShouldThrowInvalidOperation_WhenNoActiveRateCard()
    {
        var (service, rateCardStore, _, _) = Build();
        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((RateCard?)null);

        var act = () => service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rate card*");
    }

    [Fact]
    public async Task GenerateAsync_ShouldReturnEmptyInvoice_WhenNoUsage()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>());

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        invoice.TenantId.Should().Be(Tenant1);
        invoice.Currency.Should().Be("USD");
        invoice.LineItems.Should().BeEmpty();
        invoice.Subtotal.Should().Be(0m);
        invoice.Total.Should().Be(0m);
        invoice.Status.Should().Be(InvoiceStatus.Draft);
    }

    [Fact]
    public async Task GenerateAsync_ShouldCalculateFlatRate_WhenNoTiers()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1,
                    PeriodStart = PeriodStart,
                    PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound,
                    TotalQuantity = 200m,
                    RecordCount = 50,
                    LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        invoice.LineItems.Should().HaveCount(1);
        var line = invoice.LineItems[0];
        line.UsageType.Should().Be(UsageType.VoiceInbound);
        line.Quantity.Should().Be(200m);
        line.UnitPrice.Should().Be(0.05m);
        line.Amount.Should().Be(10.00m);
        line.IncludedQuantity.Should().Be(0m);
        line.OverageQuantity.Should().Be(200m);
        invoice.Subtotal.Should().Be(10.00m);
        invoice.Total.Should().Be(10.00m);
    }

    [Fact]
    public async Task GenerateAsync_ShouldSubtractIncludedQuantity_WhenConfigured()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.SmsOutbound, UnitPrice = 0.02m, IncludedQuantity = 100m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.SmsOutbound, TotalQuantity = 250m,
                    RecordCount = 250, LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        var line = invoice.LineItems[0];
        line.Quantity.Should().Be(250m);
        line.IncludedQuantity.Should().Be(100m);
        line.OverageQuantity.Should().Be(150m);
        line.Amount.Should().Be(3.00m);
    }

    [Fact]
    public async Task GenerateAsync_ShouldReturnZeroAmount_WhenUsageBelowIncluded()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.SmsOutbound, UnitPrice = 0.02m, IncludedQuantity = 500m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.SmsOutbound, TotalQuantity = 80m,
                    RecordCount = 80, LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        var line = invoice.LineItems[0];
        line.OverageQuantity.Should().Be(0m);
        line.Amount.Should().Be(0m);
    }

    [Fact]
    public async Task GenerateAsync_ShouldApplyTieredPricing_WhenTiersConfigured()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry
            {
                UsageType = UsageType.VoiceInbound,
                UnitPrice = 0.10m,
                Tiers = new List<RateTier>
                {
                    new() { FromQuantity = 0m, ToQuantity = 100m, UnitPrice = 0.10m },
                    new() { FromQuantity = 100m, ToQuantity = 500m, UnitPrice = 0.08m },
                    new() { FromQuantity = 500m, ToQuantity = null, UnitPrice = 0.05m },
                },
            });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound, TotalQuantity = 350m,
                    RecordCount = 100, LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        // Tier 1: 100 × $0.10 = $10.00
        // Tier 2: 250 × $0.08 = $20.00
        // Total = $30.00
        var line = invoice.LineItems[0];
        line.Quantity.Should().Be(350m);
        line.Amount.Should().Be(30.00m);
        invoice.Subtotal.Should().Be(30.00m);
    }

    [Fact]
    public async Task GenerateAsync_ShouldHandleMultipleUsageTypes()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m },
            new RateEntry { UsageType = UsageType.SmsOutbound, UnitPrice = 0.02m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound, TotalQuantity = 100m,
                    RecordCount = 25, LastUpdatedAt = FixedNow,
                },
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.SmsOutbound, TotalQuantity = 50m,
                    RecordCount = 50, LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        invoice.LineItems.Should().HaveCount(2);
        invoice.Subtotal.Should().Be(6.00m);
    }

    [Fact]
    public async Task GenerateAsync_ShouldSkipUsageTypes_WhenNotInRateCard()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound, TotalQuantity = 100m,
                    RecordCount = 25, LastUpdatedAt = FixedNow,
                },
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.SmsOutbound, TotalQuantity = 50m,
                    RecordCount = 50, LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        invoice.LineItems.Should().HaveCount(1);
        invoice.LineItems[0].UsageType.Should().Be(UsageType.VoiceInbound);
    }

    [Fact]
    public async Task GenerateAsync_ShouldApplyTieredPricing_WithUnboundedLastTier()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry
            {
                UsageType = UsageType.VoiceInbound,
                UnitPrice = 0m,
                Tiers = new List<RateTier>
                {
                    new() { FromQuantity = 0m, ToQuantity = 100m, UnitPrice = 0.10m },
                    new() { FromQuantity = 100m, ToQuantity = null, UnitPrice = 0.05m },
                },
            });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound, TotalQuantity = 600m,
                    RecordCount = 200, LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        // Tier 1: 100 × $0.10 = $10.00
        // Tier 2: 500 × $0.05 = $25.00
        // Total = $35.00
        invoice.LineItems[0].Amount.Should().Be(35.00m);
    }
}
