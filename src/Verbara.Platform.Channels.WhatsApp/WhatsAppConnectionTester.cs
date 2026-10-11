using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp.Meta;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// The WhatsApp connection test (whatsapp-works-for-real D15): asks the Graph API for the tenant's phone
/// number — <c>GET {BaseUrl}/{ApiVersion}/{PhoneNumberId}?fields=verified_name,display_phone_number</c> —
/// authenticated with the tenant's own <c>AccessToken</c>, and reports Meta's verdict. Tests the stored
/// configuration whether or not the channel is active, so an administrator can check it before switching
/// it on. A tenant without a token or phone number id gets
/// <see cref="ChannelConnectionTestCodes.ChannelNotConfigured"/> and no request is sent.
/// </summary>
public sealed class WhatsAppConnectionTester : IChannelConnectionTester
{
    /// <summary>Upper bound on the Graph API call; the named client's default (100 s) is far too long for a UI.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Longest provider-supplied text (Meta's error message, the verified name) relayed to the client.</summary>
    internal const int MaxProviderTextLength = 300;

    /// <summary>Largest Graph API response body read; a longer one is not parsed.</summary>
    internal const int MaxResponseBodyBytes = 64 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITenantChannelConfigStore _configStore;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppConnectionTester> _logger;

    public WhatsAppConnectionTester(
        IHttpClientFactory httpClientFactory,
        ITenantChannelConfigStore configStore,
        IOptions<WhatsAppOptions> options,
        ILogger<WhatsAppConnectionTester> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configStore = configStore;
        _options = options.Value;
        _logger = logger;
    }

    public ChannelType Channel => ChannelType.WhatsApp;

    public async Task<ChannelConnectionTestResult> TestAsync(TenantId tenantId, CancellationToken ct)
    {
        var config = await _configStore.GetAsync(tenantId, ChannelType.WhatsApp, ct).ConfigureAwait(false);
        var credentials = config?.Credentials;
        var accessToken = Credential(credentials, WhatsAppCredentialKeys.AccessToken);
        var phoneNumberId = Credential(credentials, WhatsAppCredentialKeys.PhoneNumberId);
        if (accessToken is null || phoneNumberId is null)
        {
            var missing = accessToken is null ? WhatsAppCredentialKeys.AccessToken : WhatsAppCredentialKeys.PhoneNumberId;
            return new ChannelConnectionTestResult(
                false, $"WhatsApp is not configured for this tenant (missing {missing}).",
                ChannelConnectionTestCodes.ChannelNotConfigured);
        }

        var apiVersion = Credential(credentials, WhatsAppCredentialKeys.ApiVersion) ?? _options.ApiVersion;
        var url = $"{_options.BaseUrl}/{Uri.EscapeDataString(apiVersion)}/{Uri.EscapeDataString(phoneNumberId)}" +
                  "?fields=verified_name,display_phone_number";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        int statusCode;
        bool succeeded;
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var client = _httpClientFactory.CreateClient(WhatsAppConnector.HttpClientName);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            statusCode = (int)response.StatusCode;
            succeeded = response.IsSuccessStatusCode;
            body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Log.ConnectionTestUnreachable(_logger, tenantId.Value, ex.GetType().Name);
            return new ChannelConnectionTestResult(
                false, "The WhatsApp Graph API could not be reached.", ChannelConnectionTestCodes.ProviderUnreachable);
        }

        Log.ConnectionTestAnswered(_logger, tenantId.Value, statusCode);
        var parsed = TryParse(body);

        if (!succeeded)
        {
            // Meta's text is relayed, but never the tenant's token (a Graph error can quote it) and never
            // unbounded.
            var message = ProviderText(parsed?.Error?.Message, accessToken)
                          ?? $"The WhatsApp Graph API answered {statusCode.ToString(CultureInfo.InvariantCulture)}.";
            return new ChannelConnectionTestResult(false, message, ChannelConnectionTestCodes.ProviderRejected);
        }

        var verifiedName = ProviderText(parsed?.VerifiedName, accessToken);
        return new ChannelConnectionTestResult(
            true,
            verifiedName is null
                ? "Connected to the WhatsApp Business phone number."
                : $"Connected to the WhatsApp Business phone number of {verifiedName}.",
            Code: null,
            VerifiedName: verifiedName);
    }

    /// <summary>
    /// <paramref name="text"/> with every occurrence of <paramref name="accessToken"/> replaced by
    /// <c>[redacted]</c> and cut to <see cref="MaxProviderTextLength"/> characters (plus an ellipsis);
    /// <see langword="null"/> when blank.
    /// </summary>
    internal static string? ProviderText(string? text, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var redacted = text.Replace(accessToken, "[redacted]", StringComparison.Ordinal);
        return redacted.Length <= MaxProviderTextLength ? redacted : string.Concat(redacted.AsSpan(0, MaxProviderTextLength), "…");
    }

    /// <summary>
    /// Reads at most <see cref="MaxResponseBodyBytes"/> of <paramref name="content"/>; a longer body comes back
    /// empty, so it is never parsed and the outcome is reported by status code alone.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBodyBytes)
            return string.Empty;

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length
               && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total > MaxResponseBodyBytes ? string.Empty : System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static MetaPhoneNumberResponse? TryParse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, WhatsAppJsonContext.Default.MetaPhoneNumberResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Credential(IReadOnlyDictionary<string, string>? credentials, string key) =>
        credentials is not null && credentials.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
