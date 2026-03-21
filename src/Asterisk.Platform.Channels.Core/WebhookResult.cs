namespace Asterisk.Platform.Channels.Core;

public sealed record WebhookResult(
    WebhookResultType Type,
    InboundMessage? Message,
    DeliveryStatusUpdate? StatusUpdate);
