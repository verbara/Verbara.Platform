using Asterisk.Platform.Core;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Channels.Core;

public interface IChannelConnector
{
    ChannelType Channel { get; }
    Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct);
    Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct);
}
