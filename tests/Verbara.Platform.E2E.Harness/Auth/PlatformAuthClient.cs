using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Verbara.Platform.E2E.Harness.Auth;

/// <summary>
/// Thin client over <c>POST /api/v1/auth/login</c>. Returns the access
/// token only — refresh-token + cookie handling are out of scope for the
/// walking skeleton (the entire scenario completes well under the 15-min
/// access-token TTL).
/// </summary>
internal sealed class PlatformAuthClient : IDisposable
{
    private readonly HttpClient _http;

    public PlatformAuthClient(string apiBaseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public void Dispose() => _http.Dispose();

    public async Task<string> LoginAsync(string email, string password, string tenant, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { email, password });
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Tenant-Id", tenant);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Login failed for {email}@{tenant}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Truncate(payload, 400)}");
        }

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("accessToken", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Login response missing 'accessToken' property for {email}@{tenant}. Body: {Truncate(payload, 400)}");
        }

        return tokenElement.GetString()
            ?? throw new InvalidOperationException("Login response 'accessToken' was null.");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
