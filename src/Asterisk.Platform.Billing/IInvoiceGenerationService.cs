using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public interface IInvoiceGenerationService
{
    Task<Invoice> GenerateAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);

    /// <summary>
    /// Generates an invoice for <paramref name="tenantId"/> using an explicitly provided
    /// <paramref name="rateCard"/> instead of the tenant's own rate card. Used by Partner
    /// billing flows where the Partner's markup rate card must be applied to Customer usage.
    /// The returned invoice is NOT saved to the store; the caller is responsible for persisting it.
    /// </summary>
    Task<Invoice> GenerateWithRateCardAsync(TenantId tenantId, RateCard rateCard, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);
}
