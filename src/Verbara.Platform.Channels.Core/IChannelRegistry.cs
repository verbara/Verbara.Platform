using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

public interface IChannelRegistry
{
    IChannelConnector GetConnector(ChannelType channel);
    IWebhookHandler GetHandler(ChannelType channel);
    ChannelConstraints GetConstraints(ChannelType channel);
    IReadOnlyList<ChannelType> AvailableChannels { get; }
}
