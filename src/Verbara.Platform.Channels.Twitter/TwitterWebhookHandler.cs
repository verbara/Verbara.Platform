using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.Twitter;

/// <summary>
/// Handles incoming X Account Activity API webhooks.
/// Validates CRC challenge-response checks (HMAC-SHA256) and parses DM events.
/// Non-DM events (tweets, follows, likes, etc.) are ignored.
/// </summary>
public sealed class TwitterWebhookHandler : IWebhookHandler
{
    private readonly TwitterOptions _options;
    private readonly ITenantChannelConfigStore _configStore;
    private readonly ILogger<TwitterWebhookHandler> _logger;

    public ChannelType Channel => ChannelType.Twitter;

    public TwitterWebhookHandler(
        IOptions<TwitterOptions> options,
        ITenantChannelConfigStore configStore,
        ILogger<TwitterWebhookHandler> logger)
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
        // CRC challenge: X sends a GET with crc_token query param; in webhook POST context
        // this is surfaced as an x-twitter-webhooks-signature header that must be validated.
        if (headers.TryGetValue("x-twitter-webhooks-signature", out var signature))
        {
            var config = await _configStore.GetAsync(tenantId, Channel, ct);
            var secret = config?.Credentials.GetValueOrDefault("ApiSecret") ?? _options.ApiSecret;

            if (!ValidateHmacSignature(body.Span, signature, secret))
            {
                Log.CrcValidationFailed(_logger, tenantId.Value);
                return Ignored();
            }
        }

        return ParsePayload(body.Span);
    }

    /// <summary>
    /// Validates the HMAC-SHA256 signature from X Account Activity API.
    /// The signature header value is "sha256=base64(HMAC-SHA256(apiSecret, body))".
    /// </summary>
    internal static bool ValidateHmacSignature(ReadOnlySpan<byte> body, string signatureHeader, string secret)
    {
        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var expectedBase64 = signatureHeader[prefix.Length..];

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromBase64String(expectedBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = body.ToArray();
        var computedBytes = HMACSHA256.HashData(keyBytes, bodyBytes);

        return CryptographicOperations.FixedTimeEquals(computedBytes, expectedBytes);
    }

    /// <summary>
    /// Computes the CRC response token for a CRC challenge request.
    /// X sends a GET /?crc_token=TOKEN; this method produces the "sha256=..." response.
    /// </summary>
    internal static string ComputeCrcResponse(string crcToken, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var tokenBytes = Encoding.UTF8.GetBytes(crcToken);
        var hash = HMACSHA256.HashData(keyBytes, tokenBytes);
        return $"sha256={Convert.ToBase64String(hash)}";
    }

    private WebhookResult ParsePayload(ReadOnlySpan<byte> body)
    {
        TwitterWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, TwitterJsonContext.Default.TwitterWebhookPayload);
        }
        catch (JsonException ex)
        {
            Log.DeserializeWebhookFailed(_logger, ex);
            return Ignored();
        }

        if (payload?.DirectMessageEvents is not { Length: > 0 })
            return Ignored();

        // Take the first DM event — each X webhook POST contains one event
        var dmEvent = payload.DirectMessageEvents[0];

        if (dmEvent.Type is not "message_create")
        {
            Log.EventIgnored(_logger, dmEvent.Type);
            return Ignored();
        }

        var inbound = ParseDmEvent(dmEvent, payload.Users);
        if (inbound is null)
            return Ignored();

        return new WebhookResult(WebhookResultType.NewMessage, inbound, null);
    }

    private static InboundMessage? ParseDmEvent(TwitterDmEvent dmEvent, Dictionary<string, TwitterUser>? users)
    {
        var senderId = dmEvent.MessageCreate?.SenderId;
        var messageId = dmEvent.Id;
        var text = dmEvent.MessageCreate?.MessageData?.Text;

        if (senderId is null || messageId is null || text is null)
            return null;

        var from = new ChannelAddress(ChannelType.Twitter, senderId);
        var block = new TextBlock(text);
        var envelope = new MessageEnvelope([block]);

        DateTimeOffset timestamp;
        if (long.TryParse(dmEvent.CreatedTimestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMs))
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
        else
            timestamp = DateTimeOffset.UtcNow;

        return new InboundMessage(from, envelope, messageId, timestamp);
    }

    private static WebhookResult Ignored() =>
        new(WebhookResultType.Ignored, null, null);
}
