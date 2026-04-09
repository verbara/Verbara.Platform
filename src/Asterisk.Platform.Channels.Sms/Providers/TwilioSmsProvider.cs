using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Sms.Providers;

public sealed class TwilioSmsProvider : ISmsProvider
{
    private readonly HttpClient _client;
    private readonly TwilioOptions _options;

    public TwilioSmsProvider(IHttpClientFactory httpClientFactory, IOptions<TwilioOptions> options)
    {
        _client = httpClientFactory.CreateClient("twilio");
        _options = options.Value;
    }

    public async Task<SmsSendResult> SendAsync(
        string from, string recipient, string body, CancellationToken ct)
    {
        var url = $"https://api.twilio.com/2010-04-01/Accounts/{_options.AccountSid}/Messages.json";

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

        var response = await _client.SendAsync(request, ct);
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

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.AccountSid}:{_options.AuthToken}")));

        var response = await _client.SendAsync(request, ct);
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
