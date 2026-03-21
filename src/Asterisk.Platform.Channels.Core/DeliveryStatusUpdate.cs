using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Channels.Core;

public sealed record DeliveryStatusUpdate(
    string ExternalMessageId,
    MessageDeliveryStatus NewStatus,
    DateTimeOffset Timestamp);
