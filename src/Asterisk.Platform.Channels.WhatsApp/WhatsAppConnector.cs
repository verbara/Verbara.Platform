using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
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
/// Sends outbound WhatsApp messages via the Meta Business API.
/// Enforces the 24-hour session window: if more than 24 hours have elapsed
/// since the last inbound message, a template message is sent instead.
/// </summary>
public sealed class WhatsAppConnector : IChannelConnector
{
    private readonly WhatsAppOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<WhatsAppConnector> _logger;

    // Tracks last inbound message time per recipient phone number (E.164)
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCustomerMessage = new();

    public ChannelType Channel => ChannelType.WhatsApp;

    public WhatsAppConnector(
        HttpClient httpClient,
        IOptions<WhatsAppOptions> options,
        ILogger<WhatsAppConnector> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.AccessToken);
    }

    /// <summary>Records the time of the last inbound customer message for a phone number.</summary>
    public void RecordCustomerMessage(string phoneNumber) =>
        _lastCustomerMessage[phoneNumber] = DateTimeOffset.UtcNow;

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var to = message.To.Address;
        var isOutsideWindow = IsOutsideSessionWindow(to);

        MetaSendRequest request;

        if (isOutsideWindow || message.TemplateId is not null)
        {
            var templateId = message.TemplateId ?? "default_template";
            Log.SendingTemplate(_logger, templateId, to, isOutsideWindow);
            request = BuildTemplateRequest(to, templateId);
        }
        else
        {
            request = BuildContentRequest(to, message.Content);
        }

        return await SendRequestAsync(request, ct).ConfigureAwait(false);
    }

    public Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        // Meta does not expose a status polling endpoint; status is pushed via webhooks.
        return Task.FromResult<MessageDeliveryStatus?>(null);
    }

    private bool IsOutsideSessionWindow(string phoneNumber)
    {
        if (!_lastCustomerMessage.TryGetValue(phoneNumber, out var lastTime))
            return true;

        return DateTimeOffset.UtcNow - lastTime > TimeSpan.FromHours(24);
    }

    private static MetaSendRequest BuildTemplateRequest(string to, string templateName)
    {
        return new MetaSendRequest
        {
            To = to,
            Type = "template",
            Template = new MetaSendTemplate { Name = templateName },
        };
    }

    private static MetaSendRequest BuildContentRequest(string to, MessageEnvelope content)
    {
        var block = content.Blocks.Count > 0 ? content.Blocks[0] : new TextBlock(string.Empty);

        return block switch
        {
            TextBlock text => new MetaSendRequest
            {
                To = to,
                Type = "text",
                Text = new MetaSendText { Body = text.Text },
            },

            ImageBlock image => new MetaSendRequest
            {
                To = to,
                Type = "image",
                Image = new MetaSendMedia { Link = image.Url, Caption = image.Caption },
            },

            AudioBlock audio => new MetaSendRequest
            {
                To = to,
                Type = "audio",
                Audio = new MetaSendMedia { Link = audio.Url },
            },

            VideoBlock video => new MetaSendRequest
            {
                To = to,
                Type = "video",
                Video = new MetaSendMedia { Link = video.Url, Caption = video.Caption },
            },

            FileBlock file => new MetaSendRequest
            {
                To = to,
                Type = "document",
                Document = new MetaSendDocument { Link = file.Url, Filename = file.FileName },
            },

            LocationBlock location => new MetaSendRequest
            {
                To = to,
                Type = "location",
                Location = new MetaSendLocation
                {
                    Latitude = location.Latitude,
                    Longitude = location.Longitude,
                    Name = location.Name,
                },
            },

            InteractiveBlock interactive => BuildInteractiveRequest(to, interactive),

            _ => new MetaSendRequest
            {
                To = to,
                Type = "text",
                Text = new MetaSendText { Body = string.Empty },
            },
        };
    }

    private static MetaSendRequest BuildInteractiveRequest(string to, InteractiveBlock interactive)
    {
        var buttons = interactive.Replies
            .Take(3)
            .Select(r => new MetaInteractiveButton
            {
                Reply = new MetaButtonReplyContent { Id = r.Id, Title = r.Title },
            })
            .ToArray();

        return new MetaSendRequest
        {
            To = to,
            Type = "interactive",
            Interactive = new MetaSendInteractive
            {
                Type = "button",
                Body = new MetaInteractiveBody { Text = interactive.Body },
                Action = new MetaInteractiveAction { Buttons = buttons },
            },
        };
    }

    private async Task<SendResult> SendRequestAsync(MetaSendRequest request, CancellationToken ct)
    {
        var url = $"{_options.BaseUrl}/{_options.ApiVersion}/{_options.PhoneNumberId}/messages";

        var json = JsonSerializer.Serialize(request, WhatsAppJsonContext.Default.MetaSendRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.HttpError(_logger, ex, url);
            return new SendResult(false, null, "HTTP_ERROR", ex.Message);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Log.ApiError(_logger, (int)response.StatusCode, responseBody);

            MetaSendResponse? errorResponse = null;
            try
            {
                errorResponse = JsonSerializer.Deserialize(
                    responseBody, WhatsAppJsonContext.Default.MetaSendResponse);
            }
            catch (JsonException) { /* ignore */ }

            var errorCode = errorResponse?.Error?.Code.ToString(CultureInfo.InvariantCulture) ??
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            var errorMessage = errorResponse?.Error?.Message ?? responseBody;

            return new SendResult(false, null, errorCode, errorMessage);
        }

        MetaSendResponse? sendResponse = null;
        try
        {
            sendResponse = JsonSerializer.Deserialize(
                responseBody, WhatsAppJsonContext.Default.MetaSendResponse);
        }
        catch (JsonException ex)
        {
            Log.DeserializeSendResponseFailed(_logger, ex);
        }

        var messageId = sendResponse?.Messages?.Length > 0
            ? sendResponse.Messages[0].Id
            : null;

        return new SendResult(true, messageId, null, null);
    }
}
