using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public interface IInvoiceStore
{
    Task SaveAsync(Invoice invoice, CancellationToken ct);
    Task<Invoice?> GetByIdAsync(TenantId tenantId, EntityId invoiceId, CancellationToken ct);
    Task<IReadOnlyList<Invoice>> ListAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct);
    Task UpdateStatusAsync(TenantId tenantId, EntityId invoiceId, InvoiceStatus status, CancellationToken ct);
}
