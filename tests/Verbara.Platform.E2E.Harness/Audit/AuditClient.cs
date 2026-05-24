using System.Net.Http.Headers;
using System.Text.Json;
using Verbara.Platform.Realtime.Contracts;
using Verbara.Platform.Realtime.Contracts.Dtos;

namespace Verbara.Platform.E2E.Harness.Audit;

/// <summary>
/// Reads <c>GET /admin/realtime/audit?since=&amp;limit=</c> from one
/// Realtime pod. The base URL is the pre-port-forwarded local address
/// served by <c>scripts/run-harness-talos.sh</c> — one URL per pod.
/// </summary>
internal sealed class AuditClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = RealtimeContractsJsonContext.Default,
    };

    public AuditClient(string baseUrl, string platformAdminAccessToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", platformAdminAccessToken);
    }

    public string BaseUrl => _baseUrl;

    public async Task<RelayOutcomePage> FetchAsync(DateTimeOffset? since, int limit, CancellationToken ct)
    {
        var query = new List<string>();
        if (since.HasValue)
        {
            query.Add($"since={Uri.EscapeDataString(since.Value.UtcDateTime.ToString("O"))}");
        }
        query.Add($"limit={limit}");

        var url = $"{_baseUrl}/admin/realtime/audit?{string.Join('&', query)}";

        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GET {url} failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Truncate(payload, 400)}");
        }

        var page = JsonSerializer.Deserialize<RelayOutcomePage>(payload, JsonOptions);
        if (page is null)
        {
            throw new InvalidOperationException($"GET {url} returned null body.");
        }
        return page;
    }

    public void Dispose() => _http.Dispose();

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
