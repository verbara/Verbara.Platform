using Verbara.Platform.Core;

namespace Verbara.Platform.Routing.Inbound;

public sealed class InboundRoutingOptions
{
    public Dictionary<ChannelType, EntityId> ChannelQueueMapping { get; set; } = new();
    public bool EnableLastAgentRouting { get; set; } = true;
    public TimeSpan LastAgentWindow { get; set; } = TimeSpan.FromHours(72);
    public TimeSpan OfferTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
