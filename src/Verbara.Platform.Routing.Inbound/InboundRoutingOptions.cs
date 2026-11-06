using Verbara.Platform.Core;

namespace Verbara.Platform.Routing.Inbound;

public sealed class InboundRoutingOptions
{
    public Dictionary<ChannelType, EntityId> ChannelQueueMapping { get; set; } = new();
}
