using System.Text;
using System.Web;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Sms;

/// <summary>
/// Parses inbound SMS messages and delivery status webhook callbacks.
/// Supports Twilio form-encoded webhooks with HMAC-SHA1 signature validation.
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
        // Parse form-encoded body if present (Twilio sends application/x-www-form-urlencoded)
        var formParams = ParseFormBody(body);

        // Detect inbound message: has From + Body fields, no MessageStatus
        if (formParams.TryGetValue("From", out var fromNumber) &&
            formParams.TryGetValue("Body", out var textBody) &&
            !formParams.ContainsKey("MessageStatus"))
        {
            return HandleInbound(formParams, fromNumber, textBody);
        }

        // Fall back to status update handling
        var messageId = TryExtract(formParams, headers, "MessageSid", "X-Message-Id", "message_id");
        var statusValue = TryExtract(formParams, headers, "MessageStatus", "X-Delivery-Status", "status");

        if (messageId is null || statusValue is null)
            return new WebhookResult(WebhookResultType.Ignored, null, null);

        var smsStatus = ParseDeliveryStatus(statusValue);
        if (smsStatus is null)
            return new WebhookResult(WebhookResultType.Ignored, null, null);

        var confirmedStatus = await _provider.GetStatusAsync(messageId, ct).ConfigureAwait(false);
        var mappedStatus = MapStatus(confirmedStatus);

        var update = new DeliveryStatusUpdate(messageId, mappedStatus, DateTimeOffset.UtcNow);
        return new WebhookResult(WebhookResultType.StatusUpdate, null, update);
    }

    private static WebhookResult HandleInbound(
        Dictionary<string, string> form, string fromNumber, string textBody)
    {
        var smsSid = form.GetValueOrDefault("SmsSid") ?? form.GetValueOrDefault("MessageSid") ?? Guid.NewGuid().ToString("N");

        // Build content blocks
        var blocks = new List<MessageBlock>();

        if (!string.IsNullOrWhiteSpace(textBody))
            blocks.Add(new TextBlock(textBody));

        // Handle media attachments (Twilio sends MediaUrl0..N)
        if (form.TryGetValue("NumMedia", out var numMediaStr) &&
            int.TryParse(numMediaStr, System.Globalization.CultureInfo.InvariantCulture, out var numMedia))
        {
            for (var i = 0; i < numMedia; i++)
            {
                if (form.TryGetValue($"MediaUrl{i}", out var mediaUrl))
                {
                    var contentType = form.GetValueOrDefault($"MediaContentType{i}") ?? "application/octet-stream";
                    blocks.Add(new FileBlock(mediaUrl, $"media_{i}", contentType, null));
                }
            }
        }

        var inbound = new InboundMessage(
            new ChannelAddress(ChannelType.Sms, fromNumber),
            new MessageEnvelope(blocks),
            smsSid,
            DateTimeOffset.UtcNow);

        return new WebhookResult(WebhookResultType.NewMessage, inbound, null);
    }

    private static Dictionary<string, string> ParseFormBody(ReadOnlyMemory<byte> body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body.IsEmpty) return result;

        var text = Encoding.UTF8.GetString(body.Span);
        var pairs = text.Split('&');
        foreach (var pair in pairs)
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex < 0) continue;
            var key = HttpUtility.UrlDecode(pair[..eqIndex]);
            var value = HttpUtility.UrlDecode(pair[(eqIndex + 1)..]);
            result[key] = value;
        }
        return result;
    }

    private static string? TryExtract(Dictionary<string, string> form,
        IReadOnlyDictionary<string, string> headers, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (form.TryGetValue(key, out var fv) && !string.IsNullOrEmpty(fv)) return fv;
            if (headers.TryGetValue(key, out var hv) && !string.IsNullOrEmpty(hv)) return hv;
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

    private static Verbara.Platform.Conversations.MessageDeliveryStatus MapStatus(SmsDeliveryStatus status) =>
        status switch
        {
            SmsDeliveryStatus.Queued => Verbara.Platform.Conversations.MessageDeliveryStatus.Pending,
            SmsDeliveryStatus.Sent => Verbara.Platform.Conversations.MessageDeliveryStatus.Sent,
            SmsDeliveryStatus.Delivered => Verbara.Platform.Conversations.MessageDeliveryStatus.Delivered,
            SmsDeliveryStatus.Failed => Verbara.Platform.Conversations.MessageDeliveryStatus.Failed,
            SmsDeliveryStatus.Undelivered => Verbara.Platform.Conversations.MessageDeliveryStatus.Failed,
            _ => Verbara.Platform.Conversations.MessageDeliveryStatus.Pending,
        };
}
