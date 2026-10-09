using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp.Meta;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// Sends outbound WhatsApp messages via the Meta Business API on behalf of the message's tenant.
/// The access token and phone number id are read from the tenant's WhatsApp channel configuration
/// at send time (there is no process-wide credential); a missing key fails the send with
/// <see cref="ChannelNotConfiguredErrorCode"/> before any request reaches Meta. A template is sent
/// only when <see cref="OutboundMessage.TemplateId"/> is set — whether the 24-hour customer-service
/// window requires one is decided by the caller from stored messages, not here.
/// </summary>
public sealed class WhatsAppConnector : IChannelConnector
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps WhatsApp HTTP calls.
    /// Registered via <c>AddWhatsApp()</c> with circuit 5/60s + retry 2/500ms + timeout 15s.
    /// </summary>
    public const string ResiliencePolicyKey = "channel.whatsapp";

    /// <summary>Name of the <see cref="IHttpClientFactory"/> client the connector sends through.</summary>
    public const string HttpClientName = nameof(WhatsAppConnector);

    /// <summary><see cref="SendResult.ErrorCode"/> when the tenant lacks an active config, token or number.</summary>
    public const string ChannelNotConfiguredErrorCode = "channel-not-configured";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITenantChannelConfigStore _configStore;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppConnector> _logger;
    private readonly ResiliencePolicy _policy;

    public ChannelType Channel => ChannelType.WhatsApp;

    public WhatsAppConnector(
        IHttpClientFactory httpClientFactory,
        ITenantChannelConfigStore configStore,
        IOptions<WhatsAppOptions> options,
        ILogger<WhatsAppConnector> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _httpClientFactory = httpClientFactory;
        _configStore = configStore;
        _options = options.Value;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var config = await _configStore.GetAsync(message.TenantId, ChannelType.WhatsApp, ct).ConfigureAwait(false);
        var credentials = config is { IsActive: true } ? config.Credentials : null;
        var accessToken = Credential(credentials, WhatsAppCredentialKeys.AccessToken);
        var phoneNumberId = Credential(credentials, WhatsAppCredentialKeys.PhoneNumberId);

        if (accessToken is null || phoneNumberId is null)
        {
            var missing = credentials is null ? "active configuration"
                : accessToken is null ? WhatsAppCredentialKeys.AccessToken
                : WhatsAppCredentialKeys.PhoneNumberId;
            Log.ChannelNotConfigured(_logger, message.TenantId.Value, missing);
            return new SendResult(
                false, null, ChannelNotConfiguredErrorCode,
                $"WhatsApp is not configured for this tenant (missing {missing}).");
        }

        var apiVersion = Credential(credentials, WhatsAppCredentialKeys.ApiVersion) ?? _options.ApiVersion;
        var to = message.To.Address;

        MetaSendRequest request;
        if (message.TemplateId is not null)
        {
            Log.SendingTemplate(_logger, message.TemplateId, to);
            request = BuildTemplateRequest(to, message.TemplateId);
        }
        else
        {
            request = BuildContentRequest(to, message.Content);
        }

        var url = $"{_options.BaseUrl}/{apiVersion}/{phoneNumberId}/messages";
        return await SendRequestAsync(request, url, accessToken, ct).ConfigureAwait(false);
    }

    public Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        // Meta does not expose a status polling endpoint; status is pushed via webhooks.
        return Task.FromResult<MessageDeliveryStatus?>(null);
    }

    private static string? Credential(IReadOnlyDictionary<string, string>? credentials, string key) =>
        credentials is not null && credentials.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

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

    private async Task<SendResult> SendRequestAsync(
        MetaSendRequest request, string url, string accessToken, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, WhatsAppJsonContext.Default.MetaSendRequest);

        HttpResponseMessage response;
        try
        {
            response = await _policy.ExecuteAsync(
                ResiliencePolicyKey,
                async innerCt =>
                {
                    // The request (and its content) must be rebuilt per attempt — it is consumed on send.
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    var client = _httpClientFactory.CreateClient(HttpClientName);
                    return await client.SendAsync(httpRequest, innerCt).ConfigureAwait(false);
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
