using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.WhatsApp.Meta;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.WhatsApp;

/// <summary>
/// Handles incoming Meta Business API webhook events for WhatsApp:
/// validates HMAC-SHA256 signatures, responds to verification challenges,
/// parses messages and delivery status updates.
/// </summary>
public sealed class WhatsAppWebhookHandler : IWebhookHandler
{
    private readonly WhatsAppOptions _options;
    private readonly ITenantChannelConfigStore _configStore;
    private readonly ILogger<WhatsAppWebhookHandler> _logger;

    public ChannelType Channel => ChannelType.WhatsApp;

    public WhatsAppWebhookHandler(
        IOptions<WhatsAppOptions> options,
        ITenantChannelConfigStore configStore,
        ILogger<WhatsAppWebhookHandler> logger)
    {
        _options = options.Value;
        _configStore = configStore;
        _logger = logger;
    }

    public async Task<WebhookResult> HandleAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        TenantId tenantId,
        CancellationToken ct)
    {
        var config = await _configStore.GetAsync(tenantId, Channel, ct);
        var secret = config?.Credentials.GetValueOrDefault("AppSecret") ?? _options.AppSecret;

        if (!ValidateSignature(body, headers, secret))
        {
            Log.HmacValidationFailed(_logger, tenantId.Value);
            return Ignored();
        }

        return ParsePayload(body.Span);
    }

    /// <summary>
    /// Handles the Meta webhook GET verification challenge.
    /// Returns the hub.challenge string if hub.verify_token matches, otherwise null.
    /// </summary>
    public string? HandleChallenge(IReadOnlyDictionary<string, string> queryParams)
    {
        if (!queryParams.TryGetValue("hub.mode", out var mode) || mode != "subscribe")
            return null;

        if (!queryParams.TryGetValue("hub.verify_token", out var token) ||
            token != _options.WebhookVerifyToken)
            return null;

        queryParams.TryGetValue("hub.challenge", out var challenge);
        return challenge;
    }

    internal static bool ValidateSignature(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers, string secret)
    {
        if (!headers.TryGetValue("x-hub-signature-256", out var signature))
            return false;

        if (!signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedHex = signature["sha256=".Length..];

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var hashBytes = HMACSHA256.HashData(keyBytes, body.Span);
        var actualHex = Convert.ToHexString(hashBytes);

        return string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private WebhookResult ParsePayload(ReadOnlySpan<byte> body)
    {
        MetaWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, WhatsAppJsonContext.Default.MetaWebhookPayload);
        }
        catch (JsonException ex)
        {
            Log.DeserializeWebhookFailed(_logger, ex);
            return Ignored();
        }

        if (payload?.Entry is null)
            return Ignored();

        foreach (var entry in payload.Entry)
        {
            if (entry.Changes is null) continue;

            foreach (var change in entry.Changes)
            {
                if (change.Field != "messages") continue;
                var value = change.Value;
                if (value is null) continue;

                if (value.Statuses is { Length: > 0 } statuses)
                {
                    var update = ParseStatusUpdate(statuses[0]);
                    if (update is not null)
                        return new WebhookResult(WebhookResultType.StatusUpdate, null, update);
                }

                if (value.Messages is { Length: > 0 } messages)
                {
                    var inbound = ParseInboundMessage(messages[0]);
                    if (inbound is not null)
                        return new WebhookResult(WebhookResultType.NewMessage, inbound, null);
                }
            }
        }

        return Ignored();
    }

    private static DeliveryStatusUpdate? ParseStatusUpdate(MetaStatusUpdate status)
    {
        if (status.Id is null || status.Status is null)
            return null;

        var deliveryStatus = status.Status switch
        {
            "sent" => MessageDeliveryStatus.Sent,
            "delivered" => MessageDeliveryStatus.Delivered,
            "read" => MessageDeliveryStatus.Read,
            "failed" => MessageDeliveryStatus.Failed,
            _ => MessageDeliveryStatus.Pending,
        };

        var timestamp = ParseTimestamp(status.Timestamp);
        return new DeliveryStatusUpdate(status.Id, deliveryStatus, timestamp);
    }

    private static InboundMessage? ParseInboundMessage(MetaInboundMessage msg)
    {
        if (msg.From is null || msg.Id is null || msg.Type is null)
            return null;

        MessageBlock? block = msg.Type switch
        {
            "text" when msg.Text?.Body is not null =>
                new TextBlock(msg.Text.Body),

            "image" when msg.Image is not null =>
                new ImageBlock(
                    msg.Image.Link ?? $"whatsapp-media:{msg.Image.Id}",
                    msg.Image.Caption,
                    msg.Image.MimeType),

            "audio" when msg.Audio is not null =>
                new AudioBlock(
                    msg.Audio.Link ?? $"whatsapp-media:{msg.Audio.Id}",
                    null,
                    msg.Audio.MimeType),

            "video" when msg.Video is not null =>
                new VideoBlock(
                    msg.Video.Link ?? $"whatsapp-media:{msg.Video.Id}",
                    msg.Video.Caption,
                    msg.Video.MimeType),

            "document" when msg.Document is not null =>
                new FileBlock(
                    msg.Document.Link ?? $"whatsapp-media:{msg.Document.Id}",
                    msg.Document.Filename ?? "document",
                    msg.Document.MimeType,
                    null),

            "location" when msg.Location is not null =>
                new LocationBlock(
                    msg.Location.Latitude,
                    msg.Location.Longitude,
                    msg.Location.Name),

            "interactive" when msg.Interactive?.ButtonReply is not null =>
                new TextBlock(msg.Interactive.ButtonReply.Title ?? msg.Interactive.ButtonReply.Id ?? string.Empty),

            "interactive" when msg.Interactive?.ListReply is not null =>
                new TextBlock(msg.Interactive.ListReply.Title ?? msg.Interactive.ListReply.Id ?? string.Empty),

            _ => null,
        };

        if (block is null)
            return null;

        var from = new ChannelAddress(ChannelType.WhatsApp, msg.From);
        var envelope = new MessageEnvelope([block]);
        var timestamp = ParseTimestamp(msg.Timestamp);

        return new InboundMessage(from, envelope, msg.Id, timestamp);
    }

    private static DateTimeOffset ParseTimestamp(string? unixSeconds)
    {
        if (long.TryParse(unixSeconds, out var ts))
            return DateTimeOffset.FromUnixTimeSeconds(ts);
        return DateTimeOffset.UtcNow;
    }

    private static WebhookResult Ignored() =>
        new(WebhookResultType.Ignored, null, null);
}
