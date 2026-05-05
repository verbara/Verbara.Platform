using System.Text;
using System.Text.Json;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.Twitter;

/// <summary>
/// Sends outbound DMs via the Twitter/X DM API v2.
/// Only text DMs are supported; the X DM API v2 does not support rich media attachments via direct send.
/// ImageBlock and FileBlock are silently skipped (see <see cref="TwitterMessageTransformer"/>).
/// X does not expose delivery receipt polling; <see cref="GetStatusAsync"/> always returns null.
/// </summary>
public sealed class TwitterConnector : IChannelConnector
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps Twitter/X HTTP calls.
    /// Registered via <c>AddTwitter()</c> with circuit 5/60s + retry 2/500ms + timeout 15s.
    /// </summary>
    public const string ResiliencePolicyKey = "channel.twitter";

    private readonly TwitterOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TwitterConnector> _logger;
    private readonly ResiliencePolicy _policy;

    public ChannelType Channel => ChannelType.Twitter;

    public TwitterConnector(
        HttpClient httpClient,
        IOptions<TwitterOptions> options,
        ILogger<TwitterConnector> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var participantId = message.To.Address;

        var block = message.Content.Blocks.Count > 0
            ? message.Content.Blocks[0]
            : new TextBlock(string.Empty);

        return block switch
        {
            TextBlock text => await SendDmAsync(participantId, text.Text, ct).ConfigureAwait(false),
            InteractiveBlock interactive => await SendDmWithQuickRepliesAsync(participantId, interactive, ct).ConfigureAwait(false),
            // ImageBlock/FileBlock are not supported by DM API v2 — skip
            _ => new SendResult(false, null, "UNSUPPORTED_BLOCK", $"Block type {block.GetType().Name} is not supported by the X DM API v2."),
        };
    }

    public Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        // X API v2 does not expose delivery receipt polling.
        return Task.FromResult<MessageDeliveryStatus?>(null);
    }

    private Task<SendResult> SendDmAsync(string participantId, string text, CancellationToken ct)
    {
        var request = new TwitterDmSendRequest { Text = text };
        var json = JsonSerializer.Serialize(request, TwitterJsonContext.Default.TwitterDmSendRequest);
        return PostDmAsync(participantId, json, ct);
    }

    private Task<SendResult> SendDmWithQuickRepliesAsync(string participantId, InteractiveBlock interactive, CancellationToken ct)
    {
        const int maxOptions = 20;

        var options = interactive.Replies
            .Take(maxOptions)
            .Select(r => new TwitterQuickReplyOption
            {
                Label = r.Title.Length > 36 ? r.Title[..36] : r.Title,
                Metadata = r.Id,
            })
            .ToArray();

        var request = new TwitterDmWithQuickReplyRequest
        {
            Text = interactive.Body,
            QuickReply = options.Length > 0
                ? new TwitterQuickReplyRequest { Type = "options", Options = options }
                : null,
        };

        var json = JsonSerializer.Serialize(request, TwitterJsonContext.Default.TwitterDmWithQuickReplyRequest);
        return PostDmAsync(participantId, json, ct);
    }

    private async Task<SendResult> PostDmAsync(string participantId, string json, CancellationToken ct)
    {
        var url = $"{_options.BaseUrl}/{_options.ApiVersion}/dm_conversations/with/{participantId}/messages";

        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.BearerToken}");

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

        if (response.IsSuccessStatusCode)
        {
            TwitterDmSendResponse? apiResponse = null;
            try
            {
                apiResponse = JsonSerializer.Deserialize(responseBody, TwitterJsonContext.Default.TwitterDmSendResponse);
            }
            catch (JsonException ex)
            {
                Log.DeserializeSendResponseFailed(_logger, ex);
            }

            return new SendResult(true, apiResponse?.DmEventId, null, null);
        }

        Log.ApiError(_logger, (int)response.StatusCode, responseBody);
        return new SendResult(false, null, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
    }
}
