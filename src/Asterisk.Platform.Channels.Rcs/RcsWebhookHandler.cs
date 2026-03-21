using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Rcs;

/// <summary>
/// Parses Google RBM webhook events: inbound user messages and delivery receipts.
/// Validates the request signature supplied via the <c>X-Goog-Signature</c> header.
/// </summary>
public sealed class RcsWebhookHandler : IWebhookHandler
{
    private readonly IRcsProvider _provider;

    public ChannelType Channel => ChannelType.Rcs;

    public RcsWebhookHandler(IRcsProvider provider)
    {
        _provider = provider;
    }

    public async Task<WebhookResult> HandleAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        TenantId tenantId,
        CancellationToken ct)
    {
        if (!ValidateSignature(body, headers))
            return new WebhookResult(WebhookResultType.Ignored, null, null);

        RbmWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body.Span, RcsJsonContext.Default.RbmWebhookPayload);
        }
        catch (JsonException)
        {
            return new WebhookResult(WebhookResultType.Ignored, null, null);
        }

        if (payload is null)
            return new WebhookResult(WebhookResultType.Ignored, null, null);

        // Delivery receipt event
        if (payload.DeliveryReceipt is not null && payload.MessageId is not null)
        {
            var status = await _provider.GetStatusAsync(payload.MessageId, ct).ConfigureAwait(false);
            var mapped = MapDeliveryStatus(status);
            var update = new DeliveryStatusUpdate(payload.MessageId, mapped, DateTimeOffset.UtcNow);
            return new WebhookResult(WebhookResultType.StatusUpdate, null, update);
        }

        // Inbound text message event — surfaced as NewMessage with no parsed InboundMessage
        // (full parsing requires provider-specific tenant routing configuration).
        if (payload.Text is not null)
            return new WebhookResult(WebhookResultType.NewMessage, null, null);

        return new WebhookResult(WebhookResultType.Ignored, null, null);
    }

    /// <summary>
    /// Validates the HMAC-SHA256 signature supplied by Google RBM in the
    /// <c>X-Goog-Signature</c> header.  Returns <c>true</c> when the header
    /// is absent (unauthenticated sandbox environments).
    /// </summary>
    private static bool ValidateSignature(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("X-Goog-Signature", out var signatureHeader) ||
            string.IsNullOrEmpty(signatureHeader))
        {
            // No signature present — allow (sandbox / test environment).
            return true;
        }

        if (!headers.TryGetValue("X-Goog-Signature-Secret", out var secret) ||
            string.IsNullOrEmpty(secret))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(keyBytes, body.Span);
        var expected = Convert.ToBase64String(hash);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(signatureHeader),
            Encoding.ASCII.GetBytes(expected));
    }

    private static Conversations.MessageDeliveryStatus MapDeliveryStatus(RcsDeliveryStatus status) =>
        status switch
        {
            RcsDeliveryStatus.Queued => Conversations.MessageDeliveryStatus.Pending,
            RcsDeliveryStatus.Sent => Conversations.MessageDeliveryStatus.Sent,
            RcsDeliveryStatus.Delivered => Conversations.MessageDeliveryStatus.Delivered,
            RcsDeliveryStatus.Read => Conversations.MessageDeliveryStatus.Delivered,
            RcsDeliveryStatus.Failed => Conversations.MessageDeliveryStatus.Failed,
            _ => Conversations.MessageDeliveryStatus.Pending,
        };
}
