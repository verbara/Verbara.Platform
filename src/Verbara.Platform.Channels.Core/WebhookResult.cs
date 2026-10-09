namespace Verbara.Platform.Channels.Core;

/// <summary>
/// What one webhook delivery carried. A provider may batch several inbound messages and several
/// delivery-status updates into a single delivery, so both are lists and the endpoint processes every
/// element in order (whatsapp-works-for-real D4). <see cref="Type"/> summarises the delivery; an
/// <see cref="WebhookResultType.Ignored"/> result carries empty lists.
/// </summary>
public sealed record WebhookResult(
    WebhookResultType Type,
    IReadOnlyList<InboundMessage> Messages,
    IReadOnlyList<DeliveryStatusUpdate> StatusUpdates)
{
    /// <summary>A delivery that produced nothing to process (bad signature, unparseable, not ours).</summary>
    public static WebhookResult Ignored { get; } = new(WebhookResultType.Ignored, [], []);

    /// <summary>
    /// Builds the result of a parsed delivery: <see cref="WebhookResultType.NewMessage"/> when it carried at
    /// least one message, otherwise <see cref="WebhookResultType.StatusUpdate"/> when it carried a status,
    /// otherwise <see cref="Ignored"/>.
    /// </summary>
    public static WebhookResult From(
        IReadOnlyList<InboundMessage> messages,
        IReadOnlyList<DeliveryStatusUpdate> statusUpdates)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(statusUpdates);

        if (messages.Count > 0)
            return new WebhookResult(WebhookResultType.NewMessage, messages, statusUpdates);
        return statusUpdates.Count > 0
            ? new WebhookResult(WebhookResultType.StatusUpdate, messages, statusUpdates)
            : Ignored;
    }
}
