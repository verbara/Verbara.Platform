namespace Verbara.Platform.Billing;

public interface IDunningStore
{
    Task<DunningRecord?> GetActiveAsync(string tenantId, CancellationToken ct = default);
    Task<DunningRecord?> GetByInvoiceAsync(string invoiceId, CancellationToken ct = default);
    Task<IReadOnlyList<DunningRecord>> ListActiveAsync(CancellationToken ct = default);
    Task UpsertAsync(DunningRecord record, CancellationToken ct = default);
}
