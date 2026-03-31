using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public sealed class DefaultInvoiceGenerationService : IInvoiceGenerationService
{
    private readonly IRateCardStore _rateCardStore;
    private readonly IUsageRecordStore _usageStore;
    private readonly IClock _clock;

    public DefaultInvoiceGenerationService(IRateCardStore rateCardStore, IUsageRecordStore usageStore, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(rateCardStore);
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(clock);
        _rateCardStore = rateCardStore;
        _usageStore = usageStore;
        _clock = clock;
    }

    public async Task<Invoice> GenerateAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        var rateCard = await _rateCardStore.GetActiveAsync(tenantId, periodStart, ct)
            ?? throw new InvalidOperationException($"No active rate card found for tenant '{tenantId.Value}'.");

        var summaries = await _usageStore.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);
        var summaryByType = summaries.ToDictionary(s => s.UsageType);

        var lineItems = new List<InvoiceLineItem>();

        foreach (var rate in rateCard.Rates)
        {
            if (!summaryByType.TryGetValue(rate.UsageType, out var summary))
                continue;

            var lineItem = rate.Tiers is { Count: > 0 }
                ? CalculateTieredLineItem(rate, summary)
                : CalculateFlatLineItem(rate, summary);

            lineItems.Add(lineItem);
        }

        var subtotal = lineItems.Sum(li => li.Amount);

        return new Invoice
        {
            InvoiceId = EntityId.New(),
            TenantId = tenantId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Currency = rateCard.Currency,
            LineItems = lineItems,
            Subtotal = subtotal,
            Tax = 0m,
            Total = subtotal,
            GeneratedAt = _clock.UtcNow,
        };
    }

    private static InvoiceLineItem CalculateFlatLineItem(RateEntry rate, UsageSummary summary)
    {
        var overage = Math.Max(0m, summary.TotalQuantity - rate.IncludedQuantity);
        var amount = overage * rate.UnitPrice;

        return new InvoiceLineItem
        {
            UsageType = rate.UsageType,
            Description = rate.UsageType.ToString(),
            Quantity = summary.TotalQuantity,
            UnitPrice = rate.UnitPrice,
            Amount = amount,
            IncludedQuantity = rate.IncludedQuantity,
            OverageQuantity = overage,
        };
    }

    private static InvoiceLineItem CalculateTieredLineItem(RateEntry rate, UsageSummary summary)
    {
        var remaining = summary.TotalQuantity;
        var totalAmount = 0m;

        foreach (var tier in rate.Tiers!)
        {
            if (remaining <= 0m)
                break;

            var tierCeiling = tier.ToQuantity ?? decimal.MaxValue;
            var tierWidth = tierCeiling - tier.FromQuantity;
            var quantityInTier = Math.Min(remaining, tierWidth);

            totalAmount += quantityInTier * tier.UnitPrice;
            remaining -= quantityInTier;
        }

        return new InvoiceLineItem
        {
            UsageType = rate.UsageType,
            Description = rate.UsageType.ToString(),
            Quantity = summary.TotalQuantity,
            UnitPrice = rate.Tiers![0].UnitPrice,
            Amount = totalAmount,
            IncludedQuantity = 0m,
            OverageQuantity = summary.TotalQuantity,
        };
    }
}
