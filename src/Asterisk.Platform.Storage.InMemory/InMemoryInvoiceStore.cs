using System.Collections.Concurrent;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryInvoiceStore : IInvoiceStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Invoice> _invoices = new();

    public Task SaveAsync(Invoice invoice, CancellationToken ct)
    {
        _invoices[(invoice.TenantId, invoice.InvoiceId)] = invoice;
        return Task.CompletedTask;
    }

    public Task<Invoice?> GetByIdAsync(TenantId tenantId, EntityId invoiceId, CancellationToken ct)
    {
        _invoices.TryGetValue((tenantId, invoiceId), out var invoice);
        return Task.FromResult(invoice);
    }

    public Task<IReadOnlyList<Invoice>> ListAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct)
    {
        IReadOnlyList<Invoice> result = _invoices.Values
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.PeriodStart)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(result);
    }

    public Task UpdateStatusAsync(TenantId tenantId, EntityId invoiceId, InvoiceStatus status, CancellationToken ct)
    {
        if (_invoices.TryGetValue((tenantId, invoiceId), out var invoice))
            invoice.Status = status;

        return Task.CompletedTask;
    }
}
