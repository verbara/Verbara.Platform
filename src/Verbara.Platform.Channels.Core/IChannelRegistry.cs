using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

public interface IChannelRegistry
{
    IChannelConnector GetConnector(ChannelType channel);
    IWebhookHandler GetHandler(ChannelType channel);

    /// <summary>Returns the registered webhook handler for <paramref name="channel"/>, if any.</summary>
    bool TryGetHandler(ChannelType channel, [NotNullWhen(true)] out IWebhookHandler? handler);

    ChannelConstraints GetConstraints(ChannelType channel);

    /// <summary>Channels with a registered outbound connector.</summary>
    IReadOnlyList<ChannelType> AvailableChannels { get; }

    /// <summary>Channels with a registered inbound webhook handler.</summary>
    IReadOnlyList<ChannelType> WebhookChannels { get; }
}
