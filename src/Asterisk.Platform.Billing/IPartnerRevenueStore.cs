using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public interface IPartnerRevenueStore
{
    ValueTask<PartnerRevenueRecord?> GetByInvoiceAsync(TenantId partnerId, EntityId invoiceId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<PartnerRevenueRecord>> ListAsync(TenantId partnerId, DateTimeOffset? from, DateTimeOffset? until, CancellationToken ct = default);
    ValueTask UpsertAsync(PartnerRevenueRecord record, CancellationToken ct = default);
}
