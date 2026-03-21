using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Sms;

/// <summary>
/// Parses delivery status webhook callbacks for the SMS channel.
/// Provider-specific payload parsing is delegated to <see cref="ISmsProvider"/>.
/// </summary>
public sealed class SmsWebhookHandler : IWebhookHandler
{
    private readonly ISmsProvider _provider;

    public ChannelType Channel => ChannelType.Sms;

    public SmsWebhookHandler(ISmsProvider provider)
    {
        _provider = provider;
    }

    public async Task<WebhookResult> HandleAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        TenantId tenantId,
        CancellationToken ct)
    {
        // Attempt to extract message ID and status from a generic key-value representation.
        // Provider-specific implementations of ISmsProvider handle proprietary formats.
        var messageId = TryExtractHeader(headers, "X-Message-Id", "MessageSid", "message_id");
        var statusValue = TryExtractHeader(headers, "X-Delivery-Status", "MessageStatus", "status");

        if (messageId is null || statusValue is null)
            return new WebhookResult(WebhookResultType.Ignored, null, null);

        var smsStatus = ParseDeliveryStatus(statusValue);
        if (smsStatus is null)
            return new WebhookResult(WebhookResultType.Ignored, null, null);

        // Verify with provider to ensure the status is current.
        var confirmedStatus = await _provider.GetStatusAsync(messageId, ct).ConfigureAwait(false);
        var mappedStatus = MapStatus(confirmedStatus);

        var update = new DeliveryStatusUpdate(messageId, mappedStatus, DateTimeOffset.UtcNow);
        return new WebhookResult(WebhookResultType.StatusUpdate, null, update);
    }

    private static string? TryExtractHeader(IReadOnlyDictionary<string, string> headers, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (headers.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;
        }
        return null;
    }

    private static SmsDeliveryStatus? ParseDeliveryStatus(string value) =>
        value.ToLowerInvariant() switch
        {
            "queued" => SmsDeliveryStatus.Queued,
            "sent" or "sending" => SmsDeliveryStatus.Sent,
            "delivered" => SmsDeliveryStatus.Delivered,
            "failed" => SmsDeliveryStatus.Failed,
            "undelivered" => SmsDeliveryStatus.Undelivered,
            _ => null,
        };

    private static Asterisk.Platform.Conversations.MessageDeliveryStatus MapStatus(SmsDeliveryStatus status) =>
        status switch
        {
            SmsDeliveryStatus.Queued => Asterisk.Platform.Conversations.MessageDeliveryStatus.Pending,
            SmsDeliveryStatus.Sent => Asterisk.Platform.Conversations.MessageDeliveryStatus.Sent,
            SmsDeliveryStatus.Delivered => Asterisk.Platform.Conversations.MessageDeliveryStatus.Delivered,
            SmsDeliveryStatus.Failed => Asterisk.Platform.Conversations.MessageDeliveryStatus.Failed,
            SmsDeliveryStatus.Undelivered => Asterisk.Platform.Conversations.MessageDeliveryStatus.Failed,
            _ => Asterisk.Platform.Conversations.MessageDeliveryStatus.Pending,
        };
}
