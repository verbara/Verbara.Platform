using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

/// <summary>
/// Supplies the per-tenant default channel capacity (W6). Implemented in the Api layer over
/// the tenant auth-config store; Queues stays free of an Identity dependency.
/// </summary>
public interface ICapacityDefaultsProvider
{
    Task<ChannelCapacity> GetDefaultsAsync(TenantId tenantId, CancellationToken ct);
}
