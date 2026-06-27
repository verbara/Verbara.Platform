using Microsoft.Extensions.Options;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Billing;

public sealed class DefaultInvoiceGenerationService : IInvoiceGenerationService
{
    private readonly IRateCardStore _rateCardStore;
    private readonly IUsageRecordStore _usageStore;
    private readonly ITenantQuotaStore _quotaStore;
    private readonly IClock _clock;
    private readonly ICreditLedgerStore _ledger;
    private readonly long _creditTokenRatio;
    private readonly long? _inputRatio;
    private readonly long? _outputRatio;
    private readonly bool _perDirectionPricing;
    private readonly bool _ledgerInvoiceReadEnabled;

    public DefaultInvoiceGenerationService(IRateCardStore rateCardStore, IUsageRecordStore usageStore, ITenantQuotaStore quotaStore, IClock clock, ICreditLedgerStore ledger, IOptions<PlatformLlmOptions> platformOptions)
    {
        ArgumentNullException.ThrowIfNull(rateCardStore);
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(quotaStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(platformOptions);
        _rateCardStore = rateCardStore;
        _usageStore = usageStore;
        _quotaStore = quotaStore;
        _clock = clock;
        _ledger = ledger;
        _creditTokenRatio = Math.Max(1, platformOptions.Value.CreditTokenRatio);
        _inputRatio = platformOptions.Value.InputCreditTokenRatio;
        _outputRatio = platformOptions.Value.OutputCreditTokenRatio;
        // typification-llm-inout-pricing — differentiated description active when BOTH per-direction ratios set & > 0.
        _perDirectionPricing = _inputRatio is > 0 && _outputRatio is > 0;
        _ledgerInvoiceReadEnabled = platformOptions.Value.LedgerInvoiceReadEnabled;
    }

    public async Task<Invoice> GenerateAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        var rateCard = await _rateCardStore.GetActiveAsync(tenantId, periodStart, ct)
            ?? throw new InvalidOperationException($"No active rate card found for tenant '{tenantId.Value}'.");

        var summaries = await _usageStore.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);

        return await BuildInvoiceAsync(tenantId, rateCard, summaries, periodStart, periodEnd, ct);
    }

    public async Task<Invoice> GenerateWithRateCardAsync(TenantId tenantId, RateCard rateCard, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rateCard);

        var summaries = await _usageStore.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);

        return await BuildInvoiceAsync(tenantId, rateCard, summaries, periodStart, periodEnd, ct);
    }

    private async Task<Invoice> BuildInvoiceAsync(TenantId tenantId, RateCard rateCard, IReadOnlyList<UsageSummary> summaries, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        var summaryByType = summaries.ToDictionary(s => s.UsageType);

        var lineItems = new List<InvoiceLineItem>();

        foreach (var rate in rateCard.Rates)
        {
            // AiAnalysis is billed against the per-tenant AI-credit allowance (see BuildAiCreditLineItemAsync),
            // NOT the generic token-denominated rate-card IncludedQuantity — never both (no double-count).
            if (rate.UsageType == UsageType.AiAnalysis)
                continue;

            if (!summaryByType.TryGetValue(rate.UsageType, out var summary))
                continue;

            var lineItem = rate.Tiers is { Count: > 0 }
                ? CalculateTieredLineItem(rate, summary)
                : CalculateFlatLineItem(rate, summary);

            lineItems.Add(lineItem);
        }

        var aiRate = rateCard.Rates.FirstOrDefault(r => r.UsageType == UsageType.AiAnalysis);
        if (aiRate is not null)
            lineItems.Add(await BuildAiCreditLineItemAsync(tenantId, aiRate, periodStart, periodEnd, ct));

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

    // Allowance-based AI-credit overage. Consumed credits mirror DefaultQuotaEnforcementService:
    // per-direction (Σ input/inRatio + output/outRatio + unsplit/flatRatio) when BOTH direction ratios are
    // set & > 0; otherwise flat (Σtokens / CreditTokenRatio). The allowance is TenantQuota.AiCreditsMonthly
    // expressed directly in CREDITS (0 when null = pay-as-you-go). Amount = overage × rate-card UnitPrice.
    private async Task<InvoiceLineItem> BuildAiCreditLineItemAsync(TenantId tenantId, RateEntry aiRate, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        // Credit-ledger cutover (ADR-0033 addendum, Model C). When the kill-switch is on, the customer-owed
        // overage is read straight from the ledger — Σ|PostPaid debits| in the period, the already-positive
        // billable remainder (no re-Math.Max). The display Quantity (true consumed credits) is reconstructed
        // from the allowance and the post-debit balance: under-allowance ⇒ allowance−balance, over-allowance ⇒
        // allowance+overage. Default-off: the legacy usage_records computation below runs byte-for-byte unchanged.
        if (_ledgerInvoiceReadEnabled)
        {
            var ledgerQuota = await _quotaStore.GetAsync(tenantId, ct);
            var ledgerAllowance = ledgerQuota?.AiCreditsMonthly is { } la ? la : 0m;
            var ledgerOverage = await _ledger.GetPostPaidDebitsTotalAsync(tenantId, periodStart, periodEnd, ct);
            var ledgerBalance = await _ledger.GetBalanceAsync(tenantId, ct);
            var ledgerConsumed = Math.Max(0m, ledgerAllowance - ledgerBalance) + ledgerOverage;

            return new InvoiceLineItem
            {
                UsageType = UsageType.AiAnalysis,
                Description = DescribeRate(aiRate),
                Quantity = ledgerConsumed,
                UnitPrice = aiRate.UnitPrice,
                Amount = ledgerOverage * aiRate.UnitPrice,
                IncludedQuantity = ledgerAllowance,
                OverageQuantity = ledgerOverage,
            };
        }

        decimal consumedCredits;
        if (_perDirectionPricing)
        {
            var bd = await _usageStore.GetAiTokenBreakdownAsync(tenantId, UsageType.AiAnalysis, periodStart, periodEnd, ct);
            consumedCredits = bd.InputTokens / _inputRatio!.Value
                + bd.OutputTokens / _outputRatio!.Value
                + bd.UnsplitTokens / _creditTokenRatio;
        }
        else
        {
            var summary = await _usageStore.GetSummaryByTypeAsync(tenantId, UsageType.AiAnalysis, periodStart, periodEnd, ct);
            consumedCredits = (summary?.TotalQuantity ?? 0m) / _creditTokenRatio;
        }

        var quota = await _quotaStore.GetAsync(tenantId, ct);
        var allowance = quota?.AiCreditsMonthly is { } a ? a : 0m;
        var overage = Math.Max(0m, consumedCredits - allowance);

        return new InvoiceLineItem
        {
            UsageType = UsageType.AiAnalysis,
            Description = DescribeRate(aiRate),
            Quantity = consumedCredits,
            UnitPrice = aiRate.UnitPrice,
            Amount = overage * aiRate.UnitPrice,
            IncludedQuantity = allowance,
            OverageQuantity = overage,
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
