using System.Globalization;
using System.Text.Json;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Telegram;

/// <summary>
/// Handles incoming Telegram Bot API webhook updates.
/// Validates the optional X-Telegram-Bot-Api-Secret-Token header,
/// parses message types (text, photo, document, audio, video, location, contact),
/// and ignores non-message updates (edited_message, channel_post, etc.).
/// Telegram does not push delivery receipts, so status updates are not supported.
/// </summary>
public sealed class TelegramWebhookHandler : IWebhookHandler
{
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramWebhookHandler> _logger;

    public ChannelType Channel => ChannelType.Telegram;

    public TelegramWebhookHandler(
        IOptions<TelegramOptions> options,
        ILogger<TelegramWebhookHandler> logger)
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
        if (!ValidateSecret(headers))
        {
            Log.SecretValidationFailed(_logger, tenantId.Value);
            return Task.FromResult(Ignored());
        }

        return Task.FromResult(ParsePayload(body.Span));
    }

    internal bool ValidateSecret(IReadOnlyDictionary<string, string> headers)
    {
        // If no secret is configured, skip validation
        if (_options.WebhookSecret is null)
            return true;

        if (!headers.TryGetValue("x-telegram-bot-api-secret-token", out var token))
            return false;

        return string.Equals(token, _options.WebhookSecret, StringComparison.Ordinal);
    }

    private WebhookResult ParsePayload(ReadOnlySpan<byte> body)
    {
        TelegramUpdate? update;
        try
        {
            update = JsonSerializer.Deserialize(body, TelegramJsonContext.Default.TelegramUpdate);
        }
        catch (JsonException ex)
        {
            Log.DeserializeWebhookFailed(_logger, ex);
            return Ignored();
        }

        // Only handle top-level "message" field — ignore edited_message, channel_post, etc.
        if (update?.Message is null)
            return Ignored();

        var inbound = ParseInboundMessage(update.Message);
        if (inbound is null)
            return Ignored();

        return new WebhookResult(WebhookResultType.NewMessage, inbound, null);
    }

    private static InboundMessage? ParseInboundMessage(TelegramMessage msg)
    {
        if (msg.Chat is null)
            return null;

        var chatId = msg.Chat.Id.ToString(CultureInfo.InvariantCulture);
        var messageId = msg.MessageId.ToString(CultureInfo.InvariantCulture);

        MessageBlock? block = null;

        if (msg.Text is not null)
        {
            block = new TextBlock(msg.Text);
        }
        else if (msg.Photo is { Length: > 0 })
        {
            // Telegram sends multiple sizes; pick the largest (last in array)
            var largest = msg.Photo[^1];
            block = new ImageBlock(
                $"telegram-file:{largest.FileId}",
                null,
                "image/jpeg");
        }
        else if (msg.Document is not null)
        {
            block = new FileBlock(
                $"telegram-file:{msg.Document.FileId}",
                msg.Document.FileName ?? "document",
                msg.Document.MimeType,
                msg.Document.FileSize);
        }
        else if (msg.Audio is not null)
        {
            block = new AudioBlock(
                $"telegram-file:{msg.Audio.FileId}",
                TimeSpan.FromSeconds(msg.Audio.Duration),
                msg.Audio.MimeType);
        }
        else if (msg.Video is not null)
        {
            block = new VideoBlock(
                $"telegram-file:{msg.Video.FileId}",
                null,
                msg.Video.MimeType);
        }
        else if (msg.Location is not null)
        {
            block = new LocationBlock(msg.Location.Latitude, msg.Location.Longitude, null);
        }
        else if (msg.Contact is not null)
        {
            block = new TextBlock(msg.Contact.PhoneNumber ?? string.Empty);
        }

        if (block is null)
            return null;

        var from = new ChannelAddress(ChannelType.Telegram, chatId);
        var envelope = new MessageEnvelope([block]);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(msg.Date);

        return new InboundMessage(from, envelope, messageId, timestamp);
    }

    private static WebhookResult Ignored() =>
        new(WebhookResultType.Ignored, null, null);
}
