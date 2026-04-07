using System.Collections.Concurrent;
using Asterisk.Platform.Billing;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryDunningStore : IDunningStore
{
    private readonly ConcurrentDictionary<string, DunningRecord> _store = new();

    public Task<DunningRecord?> GetActiveAsync(string tenantId, CancellationToken ct = default)
    {
        var result = _store.Values.FirstOrDefault(r => r.TenantId == tenantId && r.IsActive);
        return Task.FromResult(result);
    }

    public Task<DunningRecord?> GetByInvoiceAsync(string invoiceId, CancellationToken ct = default)
    {
        var result = _store.Values.FirstOrDefault(r => r.InvoiceId == invoiceId && r.IsActive);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<DunningRecord>> ListActiveAsync(CancellationToken ct = default)
    {
        var result = _store.Values.Where(r => r.IsActive).ToList();
        return Task.FromResult<IReadOnlyList<DunningRecord>>(result);
    }

    public Task UpsertAsync(DunningRecord record, CancellationToken ct = default)
    {
        _store[record.DunningId] = record;
        return Task.CompletedTask;
    }
}
