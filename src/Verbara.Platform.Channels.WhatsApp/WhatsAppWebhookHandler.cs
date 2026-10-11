using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp.Meta;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// Handles incoming Meta Business API webhook events for WhatsApp, per tenant and fail-closed
/// (whatsapp-works-for-real D5): the body is verified with the tenant's own <c>AppSecret</c> in constant
/// time (no process-wide fallback), every message and status of a batch is returned, and each change is
/// accepted only when its <c>metadata.phone_number_id</c> is the tenant's <c>PhoneNumberId</c>. The GET
/// subscription handshake is checked against the tenant's <c>WebhookVerifyToken</c>.
/// </summary>
public sealed class WhatsAppWebhookHandler : IWebhookHandler, IWebhookSubscriptionVerifier, IDisposable
{
    /// <summary>Meter that carries <see cref="RejectedCounterName"/>.</summary>
    public const string MeterName = "Verbara.Platform.Channels.WhatsApp";

    /// <summary>Counter of webhook deliveries (or batch changes) refused, tagged with <c>reason</c>.</summary>
    public const string RejectedCounterName = "whatsapp.webhook.rejected";

    private const string SignatureHeader = "x-hub-signature-256";
    private const string SignaturePrefix = "sha256=";

    private readonly ITenantChannelConfigStore _configStore;
    private readonly ILogger<WhatsAppWebhookHandler> _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _rejected;

    public ChannelType Channel => ChannelType.WhatsApp;

    public WhatsAppWebhookHandler(
        ITenantChannelConfigStore configStore,
        ILogger<WhatsAppWebhookHandler> logger,
        IMeterFactory? meterFactory = null)
    {
        _configStore = configStore;
        _logger = logger;
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        _rejected = _meter.CreateCounter<long>(
            RejectedCounterName,
            description: "WhatsApp webhook deliveries or changes refused (missing_app_secret, bad_signature, foreign_phone_number_id).");
    }

    public async Task<WebhookResult> HandleAsync(
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        TenantId tenantId,
        CancellationToken ct)
    {
        var config = await _configStore.GetAsync(tenantId, Channel, ct).ConfigureAwait(false);
        var secret = config?.Credentials.GetValueOrDefault(WhatsAppCredentialKeys.AppSecret);

        if (string.IsNullOrEmpty(secret))
        {
            Log.AppSecretMissing(_logger, tenantId.Value);
            Reject("missing_app_secret");
            return WebhookResult.Ignored;
        }

        if (!ValidateSignature(body, headers, secret))
        {
            Log.HmacValidationFailed(_logger, tenantId.Value);
            Reject("bad_signature");
            return WebhookResult.Ignored;
        }

        var phoneNumberId = config!.Credentials.GetValueOrDefault(WhatsAppCredentialKeys.PhoneNumberId);
        return ParsePayload(body.Span, tenantId, phoneNumberId);
    }

    /// <inheritdoc />
    public async Task<string?> VerifySubscriptionAsync(
        TenantId tenantId,
        string? mode,
        string? verifyToken,
        string? challenge,
        CancellationToken ct)
    {
        if (!string.Equals(mode, "subscribe", StringComparison.Ordinal)
            || string.IsNullOrEmpty(challenge)
            || string.IsNullOrEmpty(verifyToken))
            return null;

        var config = await _configStore.GetAsync(tenantId, Channel, ct).ConfigureAwait(false);
        if (config is not { IsActive: true })
            return null;

        var expected = config.Credentials.GetValueOrDefault(WhatsAppCredentialKeys.WebhookVerifyToken);
        if (string.IsNullOrEmpty(expected))
            return null;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(verifyToken),
            Encoding.UTF8.GetBytes(expected))
            ? challenge
            : null;
    }

    /// <summary>
    /// Verifies <c>X-Hub-Signature-256</c> (HMAC-SHA256 of the raw body, keyed with
    /// <paramref name="secret"/>) with a constant-time comparison of the decoded digests.
    /// </summary>
    internal static bool ValidateSignature(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers, string secret)
    {
        if (!headers.TryGetValue(SignatureHeader, out var signature)
            || !signature.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var providedHex = signature.AsSpan(SignaturePrefix.Length);
        if (providedHex.Length != HMACSHA256.HashSizeInBytes * 2)
            return false;

        Span<byte> provided = stackalloc byte[HMACSHA256.HashSizeInBytes];
        if (Convert.FromHexString(providedHex, provided, out _, out var written) != System.Buffers.OperationStatus.Done
            || written != provided.Length)
            return false;

        Span<byte> actual = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body.Span, actual);

        return CryptographicOperations.FixedTimeEquals(actual, provided);
    }

    private WebhookResult ParsePayload(ReadOnlySpan<byte> body, TenantId tenantId, string? tenantPhoneNumberId)
    {
        MetaWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, WhatsAppJsonContext.Default.MetaWebhookPayload);
        }
        catch (JsonException ex)
        {
            Log.DeserializeWebhookFailed(_logger, ex);
            return WebhookResult.Ignored;
        }

        if (payload?.Entry is null)
            return WebhookResult.Ignored;

        var messages = new List<InboundMessage>();
        var statuses = new List<DeliveryStatusUpdate>();

        foreach (var entry in payload.Entry)
        {
            if (entry.Changes is null) continue;

            foreach (var change in entry.Changes)
            {
                if (change.Field != "messages") continue;
                var value = change.Value;
                if (value is null) continue;

                // Bind every change to the tenant whose URL received it: a Meta app shared by two tenants
                // signs both tenants' traffic with the same secret, so the signature alone is not enough.
                if (string.IsNullOrEmpty(tenantPhoneNumberId)
                    || !string.Equals(value.Metadata?.PhoneNumberId, tenantPhoneNumberId, StringComparison.Ordinal))
                {
                    Log.ForeignPhoneNumberId(_logger, tenantId.Value);
                    Reject("foreign_phone_number_id");
                    continue;
                }

                statuses.AddRange((value.Statuses ?? []).Select(ParseStatusUpdate).OfType<DeliveryStatusUpdate>());
                messages.AddRange((value.Messages ?? []).Select(ParseInboundMessage).OfType<InboundMessage>());
            }
        }

        return WebhookResult.From(messages, statuses);
    }

    /// <summary>Disposes the meter this handler records on.</summary>
    public void Dispose() => _meter.Dispose();

    private void Reject(string reason) =>
        _rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

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
}
