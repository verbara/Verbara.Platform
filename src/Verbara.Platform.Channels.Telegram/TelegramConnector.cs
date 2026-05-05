using System.Globalization;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.Telegram;

/// <summary>
/// Sends outbound messages via the Telegram Bot API.
/// Supports text (with optional inline keyboards), photos, audio, video, documents, and locations.
/// Telegram does not support delivery receipt polling; <see cref="GetStatusAsync"/> always returns null.
/// </summary>
public sealed class TelegramConnector : IChannelConnector
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps Telegram HTTP calls.
    /// Registered via <c>AddTelegram()</c> with circuit 5/45s + retry 3/300ms + timeout 10s.
    /// </summary>
    public const string ResiliencePolicyKey = "channel.telegram";

    private readonly TelegramOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramConnector> _logger;
    private readonly ResiliencePolicy _policy;

    public ChannelType Channel => ChannelType.Telegram;

    public TelegramConnector(
        HttpClient httpClient,
        IOptions<TelegramOptions> options,
        ILogger<TelegramConnector> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (!long.TryParse(message.To.Address, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId))
            return new SendResult(false, null, "INVALID_CHAT_ID", $"Cannot parse chat ID: {message.To.Address}");

        var block = message.Content.Blocks.Count > 0
            ? message.Content.Blocks[0]
            : new TextBlock(string.Empty);

        return block switch
        {
            TextBlock text => await SendTextAsync(chatId, text.Text, null, ct).ConfigureAwait(false),
            ImageBlock image => await SendPhotoAsync(chatId, image.Url, image.Caption, ct).ConfigureAwait(false),
            AudioBlock audio => await SendAudioAsync(chatId, audio.Url, ct).ConfigureAwait(false),
            VideoBlock video => await SendVideoAsync(chatId, video.Url, video.Caption, ct).ConfigureAwait(false),
            FileBlock file => await SendDocumentAsync(chatId, file.Url, ct).ConfigureAwait(false),
            LocationBlock loc => await SendLocationAsync(chatId, loc.Latitude, loc.Longitude, ct).ConfigureAwait(false),
            InteractiveBlock interactive => await SendInteractiveAsync(chatId, interactive, ct).ConfigureAwait(false),
            _ => await SendTextAsync(chatId, string.Empty, null, ct).ConfigureAwait(false),
        };
    }

    public Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        // Telegram does not expose a delivery status polling endpoint.
        return Task.FromResult<MessageDeliveryStatus?>(null);
    }

    private Task<SendResult> SendTextAsync(
        long chatId,
        string text,
        TelegramInlineKeyboardMarkup? replyMarkup,
        CancellationToken ct)
    {
        var request = new TelegramSendMessageRequest
        {
            ChatId = chatId,
            Text = text,
            ReplyMarkup = replyMarkup,
        };

        var json = JsonSerializer.Serialize(request, TelegramJsonContext.Default.TelegramSendMessageRequest);
        return PostAsync("sendMessage", json, ct);
    }

    private Task<SendResult> SendPhotoAsync(long chatId, string url, string? caption, CancellationToken ct)
    {
        var request = new TelegramSendPhotoRequest { ChatId = chatId, Photo = url, Caption = caption };
        var json = JsonSerializer.Serialize(request, TelegramJsonContext.Default.TelegramSendPhotoRequest);
        return PostAsync("sendPhoto", json, ct);
    }

    private Task<SendResult> SendAudioAsync(long chatId, string url, CancellationToken ct)
    {
        var request = new TelegramSendAudioRequest { ChatId = chatId, Audio = url };
        var json = JsonSerializer.Serialize(request, TelegramJsonContext.Default.TelegramSendAudioRequest);
        return PostAsync("sendAudio", json, ct);
    }

    private Task<SendResult> SendVideoAsync(long chatId, string url, string? caption, CancellationToken ct)
    {
        var request = new TelegramSendVideoRequest { ChatId = chatId, Video = url, Caption = caption };
        var json = JsonSerializer.Serialize(request, TelegramJsonContext.Default.TelegramSendVideoRequest);
        return PostAsync("sendVideo", json, ct);
    }

    private Task<SendResult> SendDocumentAsync(long chatId, string url, CancellationToken ct)
    {
        var request = new TelegramSendDocumentRequest { ChatId = chatId, Document = url };
        var json = JsonSerializer.Serialize(request, TelegramJsonContext.Default.TelegramSendDocumentRequest);
        return PostAsync("sendDocument", json, ct);
    }

    private Task<SendResult> SendLocationAsync(long chatId, double latitude, double longitude, CancellationToken ct)
    {
        var request = new TelegramSendLocationRequest { ChatId = chatId, Latitude = latitude, Longitude = longitude };
        var json = JsonSerializer.Serialize(request, TelegramJsonContext.Default.TelegramSendLocationRequest);
        return PostAsync("sendLocation", json, ct);
    }

    private Task<SendResult> SendInteractiveAsync(long chatId, InteractiveBlock interactive, CancellationToken ct)
    {
        var buttons = interactive.Replies
            .Select(r => new TelegramInlineKeyboardButton { Text = r.Title, CallbackData = r.Id })
            .ToArray();

        var markup = new TelegramInlineKeyboardMarkup
        {
            InlineKeyboard = [buttons],
        };

        return SendTextAsync(chatId, interactive.Body, markup, ct);
    }

    private async Task<SendResult> PostAsync(string method, string json, CancellationToken ct)
    {
        var url = $"{_options.BaseUrl}/bot{_options.BotToken}/{method}";

        HttpResponseMessage response;
        try
        {
            response = await _policy.ExecuteAsync(
                ResiliencePolicyKey,
                async innerCt =>
                {
                    // StringContent must be rebuilt per attempt — Content is consumed on send.
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    return await _httpClient.PostAsync(url, content, innerCt).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.HttpError(_logger, ex, url);
            return new SendResult(false, null, "HTTP_ERROR", ex.Message);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        TelegramApiResponse? apiResponse = null;
        try
        {
            apiResponse = JsonSerializer.Deserialize(responseBody, TelegramJsonContext.Default.TelegramApiResponse);
        }
        catch (JsonException ex)
        {
            Log.DeserializeSendResponseFailed(_logger, ex);
        }

        if (apiResponse is { Ok: true })
        {
            var messageId = apiResponse.Result?.MessageId.ToString(CultureInfo.InvariantCulture);
            return new SendResult(true, messageId, null, null);
        }

        Log.ApiError(_logger, apiResponse?.ErrorCode, apiResponse?.Description);

        var errorCode = apiResponse?.ErrorCode?.ToString(CultureInfo.InvariantCulture)
                        ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var errorMessage = apiResponse?.Description ?? responseBody;

        return new SendResult(false, null, errorCode, errorMessage);
    }
}
