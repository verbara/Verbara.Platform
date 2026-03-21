using Asterisk.Platform.Core;

namespace Asterisk.Platform.Routing.Inbound;

public interface IAgentSelector
{
    Task<EntityId?> SelectAgentAsync(
        TenantId tenantId,
        EntityId queueId,
        ChannelType channel,
        EntityId? preferredAgentId,
        CancellationToken ct);
}
