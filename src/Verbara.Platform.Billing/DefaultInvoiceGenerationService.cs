using Microsoft.Extensions.Options;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Billing;

public sealed class DefaultInvoiceGenerationService : IInvoiceGenerationService
{
    private readonly IRateCardStore _rateCardStore;
    private readonly IUsageRecordStore _usageStore;
    private readonly IClock _clock;
    private readonly bool _perDirectionPricing;

    public DefaultInvoiceGenerationService(IRateCardStore rateCardStore, IUsageRecordStore usageStore, IClock clock, IOptions<PlatformLlmOptions> platformOptions)
    {
        ArgumentNullException.ThrowIfNull(rateCardStore);
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(platformOptions);
        _rateCardStore = rateCardStore;
        _usageStore = usageStore;
        _clock = clock;
        // typification-llm-inout-pricing — differentiated description active when BOTH per-direction ratios set & > 0.
        _perDirectionPricing = platformOptions.Value.InputCreditTokenRatio is > 0
            && platformOptions.Value.OutputCreditTokenRatio is > 0;
    }

    public async Task<Invoice> GenerateAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        var rateCard = await _rateCardStore.GetActiveAsync(tenantId, periodStart, ct)
            ?? throw new InvalidOperationException($"No active rate card found for tenant '{tenantId.Value}'.");

        var summaries = await _usageStore.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);

        return BuildInvoice(tenantId, rateCard, summaries, periodStart, periodEnd);
    }

    public async Task<Invoice> GenerateWithRateCardAsync(TenantId tenantId, RateCard rateCard, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rateCard);

        var summaries = await _usageStore.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);

        return BuildInvoice(tenantId, rateCard, summaries, periodStart, periodEnd);
    }

    private Invoice BuildInvoice(TenantId tenantId, RateCard rateCard, IReadOnlyList<UsageSummary> summaries, DateTimeOffset periodStart, DateTimeOffset periodEnd)
    {
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

    // Differentiated AI-pricing reflected in the description only; AMOUNT stays rate-card/token-driven.
    private string DescribeRate(RateEntry rate) =>
        rate.UsageType == UsageType.AiAnalysis && _perDirectionPricing
            ? "AiAnalysis (input/output pricing)"
            : rate.UsageType.ToString();

    private InvoiceLineItem CalculateFlatLineItem(RateEntry rate, UsageSummary summary)
    {
        var overage = Math.Max(0m, summary.TotalQuantity - rate.IncludedQuantity);
        var amount = overage * rate.UnitPrice;

        return new InvoiceLineItem
        {
            UsageType = rate.UsageType,
            Description = DescribeRate(rate),
            Quantity = summary.TotalQuantity,
            UnitPrice = rate.UnitPrice,
            Amount = amount,
            IncludedQuantity = rate.IncludedQuantity,
            OverageQuantity = overage,
        };
    }

    private InvoiceLineItem CalculateTieredLineItem(RateEntry rate, UsageSummary summary)
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
            Description = DescribeRate(rate),
            Quantity = summary.TotalQuantity,
            UnitPrice = rate.Tiers![0].UnitPrice,
            Amount = totalAmount,
            IncludedQuantity = 0m,
            OverageQuantity = summary.TotalQuantity,
        };
    }
}
