using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.Sms.Providers;

public sealed class TwilioSmsProvider : ISmsProvider
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps Twilio HTTP calls.
    /// Registered via <c>AddTwilioResiliencePolicy()</c> with circuit 5/30s + retry 3/200ms + timeout 10s.
    /// </summary>
    public const string ResiliencePolicyKey = "channel.twilio-sms";

    private readonly HttpClient _client;
    private readonly TwilioOptions _options;
    private readonly ResiliencePolicy _policy;

    public TwilioSmsProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<TwilioOptions> options,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _client = httpClientFactory.CreateClient("twilio");
        _options = options.Value;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public async Task<SmsSendResult> SendAsync(
        string from, string recipient, string body, CancellationToken ct)
    {
        var url = $"https://api.twilio.com/2010-04-01/Accounts/{_options.AccountSid}/Messages.json";

        var response = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                // Rebuild HttpRequestMessage per attempt — FormUrlEncodedContent is consumed on send.
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["To"] = recipient,
                    ["From"] = from,
                    ["Body"] = body,
                });
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{_options.AccountSid}:{_options.AuthToken}")));
                return await _client.SendAsync(request, innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct);

        var parsed = JsonSerializer.Deserialize(json, TwilioJsonContext.Default.TwilioMessageResponse);

        if (response.IsSuccessStatusCode && parsed?.Sid is not null)
        {
            return new SmsSendResult(true, parsed.Sid, null, null);
        }

        return new SmsSendResult(
            false,
            null,
            parsed?.ErrorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parsed?.ErrorMessage ?? $"HTTP {(int)response.StatusCode}");
    }

    public async Task<SmsDeliveryStatus> GetStatusAsync(string messageId, CancellationToken ct)
    {
        var url = $"https://api.twilio.com/2010-04-01/Accounts/{_options.AccountSid}/Messages/{messageId}.json";

        var response = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{_options.AccountSid}:{_options.AuthToken}")));
                return await _client.SendAsync(request, innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return SmsDeliveryStatus.Failed;

        var json = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize(json, TwilioJsonContext.Default.TwilioMessageResponse);

        return parsed?.Status?.ToLowerInvariant() switch
        {
            "queued" or "accepted" => SmsDeliveryStatus.Queued,
            "sending" or "sent" => SmsDeliveryStatus.Sent,
            "delivered" => SmsDeliveryStatus.Delivered,
            "undelivered" => SmsDeliveryStatus.Undelivered,
            "failed" => SmsDeliveryStatus.Failed,
            _ => SmsDeliveryStatus.Queued,
        };
    }
}
