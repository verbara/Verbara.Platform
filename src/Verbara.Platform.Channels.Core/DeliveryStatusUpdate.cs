using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.Core;

public sealed record DeliveryStatusUpdate(
    string ExternalMessageId,
    MessageDeliveryStatus NewStatus,
    DateTimeOffset Timestamp);
