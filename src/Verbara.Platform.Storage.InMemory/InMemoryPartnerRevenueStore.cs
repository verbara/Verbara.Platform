using System.Collections.Concurrent;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryPartnerRevenueStore : IPartnerRevenueStore
{
    private readonly ConcurrentDictionary<string, PartnerRevenueRecord> _records = new();

    public ValueTask<PartnerRevenueRecord?> GetByInvoiceAsync(TenantId partnerId, EntityId invoiceId, CancellationToken ct)
    {
        var record = _records.Values
            .FirstOrDefault(r => r.PartnerTenantId == partnerId && r.InvoiceId == invoiceId);
        return ValueTask.FromResult(record);
    }

    public ValueTask<IReadOnlyList<PartnerRevenueRecord>> ListAsync(TenantId partnerId, DateTimeOffset? from, DateTimeOffset? until, CancellationToken ct)
    {
        var query = _records.Values
            .Where(r => r.PartnerTenantId == partnerId);

        if (from.HasValue)
            query = query.Where(r => r.PeriodEnd >= from.Value);
        if (until.HasValue)
            query = query.Where(r => r.PeriodStart <= until.Value);

        IReadOnlyList<PartnerRevenueRecord> result = query
            .OrderByDescending(r => r.PeriodStart)
            .ToList();
        return ValueTask.FromResult(result);
    }

    public ValueTask UpsertAsync(PartnerRevenueRecord record, CancellationToken ct)
    {
        _records[record.RevenueId.Value] = record;
        return ValueTask.CompletedTask;
    }
}
