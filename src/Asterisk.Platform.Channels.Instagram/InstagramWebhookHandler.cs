using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Instagram.Meta;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Instagram;

/// <summary>
/// Handles incoming Meta Graph API webhook events for Instagram DM:
/// validates HMAC-SHA256 signatures, responds to verification challenges,
/// parses DM messages and ignores comment/mention events.
/// </summary>
public sealed class InstagramWebhookHandler : IWebhookHandler
{
    private readonly InstagramOptions _options;
    private readonly ILogger<InstagramWebhookHandler> _logger;

    public ChannelType Channel => ChannelType.Instagram;

    public InstagramWebhookHandler(
        IOptions<InstagramOptions> options,
        ILogger<InstagramWebhookHandler> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<WebhookResult> HandleAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        TenantId tenantId,
        CancellationToken ct)
    {
        if (!ValidateSignature(body, headers))
        {
            Log.HmacValidationFailed(_logger, tenantId.Value);
            return Task.FromResult(Ignored());
        }

        return Task.FromResult(ParsePayload(body.Span));
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

    internal bool ValidateSignature(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("x-hub-signature-256", out var signature))
            return false;

        if (!signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedHex = signature["sha256=".Length..];

        var keyBytes = Encoding.UTF8.GetBytes(_options.AppSecret);
        var hashBytes = HMACSHA256.HashData(keyBytes, body.Span);
        var actualHex = Convert.ToHexString(hashBytes);

        return string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private WebhookResult ParsePayload(ReadOnlySpan<byte> body)
    {
        InstagramWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, InstagramJsonContext.Default.InstagramWebhookPayload);
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
            // entry[].changes[] contains comment/mention events — ignore them
            if (entry.Changes is { Length: > 0 })
                continue;

            if (entry.Messaging is null) continue;

            foreach (var ev in entry.Messaging)
            {
                // Delivery status
                if (ev.Delivery is not null && ev.Delivery.Mids is { Length: > 0 } mids)
                {
                    var update = new DeliveryStatusUpdate(
                        mids[0],
                        MessageDeliveryStatus.Delivered,
                        DateTimeOffset.FromUnixTimeMilliseconds(ev.Delivery.Watermark));
                    return new WebhookResult(WebhookResultType.StatusUpdate, null, update);
                }

                if (ev.Message is not null)
                {
                    var inbound = ParseInboundMessage(ev);
                    if (inbound is not null)
                        return new WebhookResult(WebhookResultType.NewMessage, inbound, null);
                }
            }
        }

        return Ignored();
    }

    private static InboundMessage? ParseInboundMessage(InstagramMessagingEvent ev)
    {
        var msg = ev.Message;
        if (msg is null || ev.Sender?.Id is null || msg.Mid is null)
            return null;

        MessageBlock block;

        if (msg.QuickReply?.Payload is not null)
        {
            block = new TextBlock(msg.QuickReply.Payload);
        }
        else if (msg.Text is not null)
        {
            block = new TextBlock(msg.Text);
        }
        else if (msg.Attachments is { Length: > 0 } attachments)
        {
            var att = attachments[0];
            var url = att.Payload?.Url ?? string.Empty;

            block = att.Type switch
            {
                "image" => new ImageBlock(url, null, null),
                "audio" => new AudioBlock(url, null, null),
                "video" => new VideoBlock(url, null, null),
                "file" => new FileBlock(url, "file", null, null),
                _ => new TextBlock(string.Empty),
            };
        }
        else
        {
            return null;
        }

        var from = new ChannelAddress(ChannelType.Instagram, ev.Sender.Id);
        var envelope = new MessageEnvelope([block]);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ev.Timestamp);

        return new InboundMessage(from, envelope, msg.Mid, timestamp);
    }

    private static WebhookResult Ignored() =>
        new(WebhookResultType.Ignored, null, null);
}
