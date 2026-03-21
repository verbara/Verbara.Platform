using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Messenger.Meta;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Messenger;

/// <summary>
/// Sends outbound Facebook Messenger messages via the Meta Graph API.
/// Uses Page Access Token authentication and sends to the /me/messages endpoint.
/// </summary>
public sealed class MessengerConnector : IChannelConnector
{
    private readonly MessengerOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<MessengerConnector> _logger;

    public ChannelType Channel => ChannelType.Messenger;

    public MessengerConnector(
        HttpClient httpClient,
        IOptions<MessengerOptions> options,
        ILogger<MessengerConnector> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.PageAccessToken);
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var request = BuildRequest(message.To.Address, message.Content);
        return await SendRequestAsync(request, ct).ConfigureAwait(false);
    }

    public Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        // Meta does not expose a status polling endpoint; status is pushed via webhooks.
        return Task.FromResult<MessageDeliveryStatus?>(null);
    }

    private static MessengerSendRequest BuildRequest(string recipientId, MessageEnvelope content)
    {
        var block = content.Blocks.Count > 0 ? content.Blocks[0] : new TextBlock(string.Empty);

        return block switch
        {
            TextBlock text => BuildTextRequest(recipientId, text.Text),

            ImageBlock image => BuildAttachmentRequest(recipientId, "image", image.Url),

            InteractiveBlock interactive => BuildQuickReplyRequest(recipientId, interactive),

            _ => BuildTextRequest(recipientId, string.Empty),
        };
    }

    private static MessengerSendRequest BuildTextRequest(string recipientId, string text)
    {
        return new MessengerSendRequest
        {
            Recipient = new MessengerSendRecipient { Id = recipientId },
            Message = new MessengerSendMessage { Text = text },
        };
    }

    private static MessengerSendRequest BuildAttachmentRequest(string recipientId, string type, string url)
    {
        return new MessengerSendRequest
        {
            Recipient = new MessengerSendRecipient { Id = recipientId },
            Message = new MessengerSendMessage
            {
                Attachment = new MessengerSendAttachment
                {
                    Type = type,
                    Payload = new MessengerSendAttachmentPayload { Url = url, IsReusable = true },
                },
            },
        };
    }

    private static MessengerSendRequest BuildQuickReplyRequest(string recipientId, InteractiveBlock interactive)
    {
        var quickReplies = interactive.Replies
            .Take(13) // Messenger allows up to 13 quick replies
            .Select(r => new MessengerSendQuickReply
            {
                Title = r.Title.Length > 20 ? r.Title[..20] : r.Title,
                Payload = r.Id,
            })
            .ToArray();

        return new MessengerSendRequest
        {
            Recipient = new MessengerSendRecipient { Id = recipientId },
            Message = new MessengerSendMessage
            {
                Text = interactive.Body,
                QuickReplies = quickReplies,
            },
        };
    }

    private async Task<SendResult> SendRequestAsync(MessengerSendRequest request, CancellationToken ct)
    {
        var url = $"{_options.BaseUrl}/{_options.ApiVersion}/me/messages";

        var json = JsonSerializer.Serialize(request, MessengerJsonContext.Default.MessengerSendRequest);
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

            MessengerSendResponse? errorResponse = null;
            try
            {
                errorResponse = JsonSerializer.Deserialize(
                    responseBody, MessengerJsonContext.Default.MessengerSendResponse);
            }
            catch (JsonException) { /* ignore */ }

            var errorCode = errorResponse?.Error?.Code.ToString(CultureInfo.InvariantCulture) ??
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            var errorMessage = errorResponse?.Error?.Message ?? responseBody;

            return new SendResult(false, null, errorCode, errorMessage);
        }

        MessengerSendResponse? sendResponse = null;
        try
        {
            sendResponse = JsonSerializer.Deserialize(
                responseBody, MessengerJsonContext.Default.MessengerSendResponse);
        }
        catch (JsonException ex)
        {
            Log.DeserializeSendResponseFailed(_logger, ex);
        }

        return new SendResult(true, sendResponse?.MessageId, null, null);
    }
}
