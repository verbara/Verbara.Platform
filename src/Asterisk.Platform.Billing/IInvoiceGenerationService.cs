using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public interface IInvoiceGenerationService
{
    Task<Invoice> GenerateAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);
}
