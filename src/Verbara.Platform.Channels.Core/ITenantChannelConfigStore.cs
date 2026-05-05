using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

public interface ITenantChannelConfigStore
{
    Task<TenantChannelConfig?> GetAsync(TenantId tenantId, ChannelType channel, CancellationToken ct);
    Task SaveAsync(TenantChannelConfig config, CancellationToken ct);
}
