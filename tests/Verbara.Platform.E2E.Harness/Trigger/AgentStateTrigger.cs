using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Verbara.Platform.E2E.Harness.Trigger;

/// <summary>
/// Generates <c>AgentStateChangedEvent</c>s by toggling the agent's state
/// via <c>PUT /api/v1/agents/me/state</c>. Each successful PUT publishes
/// exactly one event to <c>PlatformEventBus</c> which the Pro.Push.Redis
/// backplane forwards to every Realtime pod's <c>PushToHubRelay</c> —
/// the leader pod forwards, the others skip per ADR-0022 Phase A.5.
/// </summary>
/// <remarks>
/// We alternate <c>Ready ↔ Busy</c> on every call (vs. POST-create
/// conversations) because the agent path requires zero pre-seed beyond
/// the user record itself — no contacts, queues, or channel config.
/// </remarks>
internal sealed class AgentStateTrigger : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _tenant;
    // Valid AgentState enum values (src/Verbara.Platform.Queues/AgentState.cs):
    // Offline, Available, Busy, Break, Lunch, Training, ACW, DND. Alternating
    // Available ↔ Busy guarantees a state transition on every PUT (the API
    // publishes ConversationStateChangedEvent only when oldState != newState
    // — same Switchboard.AcceptAsync pattern).
    private static readonly string[] States = ["Available", "Busy"];

    public AgentStateTrigger(string apiBaseUrl, string accessToken, string tenant)
    {
        _tenant = tenant;
        _http = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        _http.DefaultRequestHeaders.Add("X-Tenant-Id", _tenant);
    }

    /// <summary>
    /// Emits <paramref name="count"/> agent-state transitions sequentially.
    /// Returns the recorded ISO timestamps so the scenario can correlate
    /// audit-endpoint rows with the trigger window.
    /// </summary>
    public async Task<IReadOnlyList<DateTimeOffset>> EmitAsync(int count, CancellationToken ct)
    {
        var stamps = new List<DateTimeOffset>(count);
        for (var i = 0; i < count; i++)
        {
            var state = States[i % States.Length];
            await PutStateAsync(state, ct).ConfigureAwait(false);
            stamps.Add(DateTimeOffset.UtcNow);
        }
        return stamps;
    }

    private async Task PutStateAsync(string state, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { state });
        using var req = new HttpRequestMessage(HttpMethod.Put, "api/v1/agents/me/state")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"PUT /agents/me/state {state} failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Truncate(payload, 400)}");
        }
    }

    public void Dispose() => _http.Dispose();

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
