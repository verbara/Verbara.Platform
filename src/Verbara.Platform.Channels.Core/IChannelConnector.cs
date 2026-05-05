using Verbara.Platform.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.Core;

public interface IChannelConnector
{
    ChannelType Channel { get; }
    Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct);
    Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct);
}
