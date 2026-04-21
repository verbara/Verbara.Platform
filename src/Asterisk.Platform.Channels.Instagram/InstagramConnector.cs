using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Instagram.Meta;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Instagram;

/// <summary>
/// Sends outbound Instagram DM messages via the Meta Graph API.
/// Uses Page Access Token authentication and sends to the /me/messages endpoint.
/// Note: Instagram does not support button templates; use quick replies instead.
/// </summary>
public sealed class InstagramConnector : IChannelConnector
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps Instagram HTTP calls.
    /// Registered via <c>AddInstagram()</c> with circuit 5/60s + retry 2/500ms + timeout 15s.
    /// </summary>
    public const string ResiliencePolicyKey = "channel.instagram";

    private readonly InstagramOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<InstagramConnector> _logger;
    private readonly ResiliencePolicy _policy;

    public ChannelType Channel => ChannelType.Instagram;

    public InstagramConnector(
        HttpClient httpClient,
        IOptions<InstagramOptions> options,
        ILogger<InstagramConnector> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;

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

    private static InstagramSendRequest BuildRequest(string recipientId, MessageEnvelope content)
    {
        var block = content.Blocks.Count > 0 ? content.Blocks[0] : new TextBlock(string.Empty);

        return block switch
        {
            TextBlock text => BuildTextRequest(recipientId, text.Text),

            ImageBlock image => BuildAttachmentRequest(recipientId, "image", image.Url),

            // Instagram does NOT support button templates — fall back to quick replies
            InteractiveBlock interactive => BuildQuickReplyRequest(recipientId, interactive),

            _ => BuildTextRequest(recipientId, string.Empty),
        };
    }

    private static InstagramSendRequest BuildTextRequest(string recipientId, string text)
    {
        return new InstagramSendRequest
        {
            Recipient = new InstagramSendRecipient { Id = recipientId },
            Message = new InstagramSendMessage { Text = text },
        };
    }

    private static InstagramSendRequest BuildAttachmentRequest(string recipientId, string type, string url)
    {
        return new InstagramSendRequest
        {
            Recipient = new InstagramSendRecipient { Id = recipientId },
            Message = new InstagramSendMessage
            {
                Attachment = new InstagramSendAttachment
                {
                    Type = type,
                    Payload = new InstagramSendAttachmentPayload { Url = url, IsReusable = true },
                },
            },
        };
    }

    private static InstagramSendRequest BuildQuickReplyRequest(string recipientId, InteractiveBlock interactive)
    {
        var quickReplies = interactive.Replies
            .Take(13) // Instagram follows Messenger limit of 13 quick replies
            .Select(r => new InstagramSendQuickReply
            {
                Title = r.Title.Length > 20 ? r.Title[..20] : r.Title,
                Payload = r.Id,
            })
            .ToArray();

        return new InstagramSendRequest
        {
            Recipient = new InstagramSendRecipient { Id = recipientId },
            Message = new InstagramSendMessage
            {
                Text = interactive.Body,
                QuickReplies = quickReplies,
            },
        };
    }

    private async Task<SendResult> SendRequestAsync(InstagramSendRequest request, CancellationToken ct)
    {
        var url = $"{_options.BaseUrl}/{_options.ApiVersion}/me/messages";

        var json = JsonSerializer.Serialize(request, InstagramJsonContext.Default.InstagramSendRequest);

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

        if (!response.IsSuccessStatusCode)
        {
            Log.ApiError(_logger, (int)response.StatusCode, responseBody);

            InstagramSendResponse? errorResponse = null;
            try
            {
                errorResponse = JsonSerializer.Deserialize(
                    responseBody, InstagramJsonContext.Default.InstagramSendResponse);
            }
            catch (JsonException) { /* ignore */ }

            var errorCode = errorResponse?.Error?.Code.ToString(CultureInfo.InvariantCulture) ??
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            var errorMessage = errorResponse?.Error?.Message ?? responseBody;

            return new SendResult(false, null, errorCode, errorMessage);
        }

        InstagramSendResponse? sendResponse = null;
        try
        {
            sendResponse = JsonSerializer.Deserialize(
                responseBody, InstagramJsonContext.Default.InstagramSendResponse);
        }
        catch (JsonException ex)
        {
            Log.DeserializeSendResponseFailed(_logger, ex);
        }

        return new SendResult(true, sendResponse?.MessageId, null, null);
    }
}
