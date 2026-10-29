using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Ending an impersonation must not depend on the target tenant's status (licensed-agent-metering-follow-ups,
/// design D1, issue #344): a session into a tenant suspended mid-session must still end, release its slot in the
/// impersonator's concurrent-session count, and leave its token revoked. The exemption is one routed endpoint —
/// <c>DELETE /management/impersonate</c> — and nothing else the token can reach is served while its tenant is
/// suspended.
/// </summary>
/// <remarks>
/// Every test drives a Partner actor of its own (<see cref="ImpersonationApiFactory.NewPartnerActorAsync"/>), so
/// suspending its Customer and counting its sessions touches no other suite's tenant or session.
/// </remarks>
public sealed partial class ImpersonationSuspendedTenantTests : IClassFixture<ImpersonationApiFactory>, IAsyncLifetime
{
    /// <summary>The tenant-status middleware's own refusal text for a suspended tenant (the prose, uncoded variant).</summary>
    private const string SuspendedProse = "This tenant account has been suspended. Contact your administrator.";

    private const string ExemptRoute = "DELETE /api/v{version:apiVersion}/management/impersonate";

    private readonly ImpersonationApiFactory _factory;
    private readonly List<string> _startedSessions = [];

    public ImpersonationSuspendedTenantTests(ImpersonationApiFactory factory) => _factory = factory;

    // ── Regression (#344) and design D1 test 5 ────────────────────────────────

    [Theory]
    [InlineData("/api/v1/management/impersonate")]
    [InlineData("/api/v1.0/management/impersonate")]
    public async Task EndImpersonation_ShouldEndSessionAndReleaseCount_WhenTargetTenantSuspendedMidSession(string endPath)
    {
        var actor = await _factory.NewPartnerActorAsync(impersonationTimeoutMinutes: 30);
        await LimitConcurrentSessionsAsync(actor, maxConcurrent: 1);
        var session = await StartAsync(actor);
        (await GetContactsAsync(session.AccessToken)).Status.Should().Be(HttpStatusCode.OK,
            because: "the token works in its target tenant while that tenant is Active");
        var countBefore = await ActiveSessionCountAsync(actor);
        countBefore.Should().Be(1);

        await SetTenantStatusAsync(actor.CustomerTenantId, TenantStatus.Suspended);

        var (status, body) = await SendAsync(HttpMethod.Delete, endPath, session.AccessToken);
        var countAfter = await ActiveSessionCountAsync(actor);

        status.Should().Be(HttpStatusCode.NoContent,
            because: $"ending an impersonation must not depend on the target tenant's status "
                   + $"(response body: {body}; active sessions before: {countBefore}, after: {countAfter})");
        (await _factory.SessionStatusAsync(session.SessionId)).Should().Be(ImpersonationSessionStatus.Completed);
        countAfter.Should().Be(countBefore - 1,
            because: "an ended session must stop counting against the impersonator's concurrent-session limit");
        (await GetContactsAsync(session.AccessToken)).Status.Should().Be(HttpStatusCode.Unauthorized,
            because: "an ended impersonation's token authenticates nothing, whatever its tenant's status");

        // The slot is free again: with a limit of one, a new session starts (the target must be Active to start).
        await SetTenantStatusAsync(actor.CustomerTenantId, TenantStatus.Active);
        await StartAsync(actor);
    }

    // ── Design D1 test 1: the suspended tenant still refuses past the earlier gates ──

    [Fact]
    public async Task Contacts_ShouldAnswerSuspendedRefusal_WhenImpersonatedTenantSuspended()
    {
        var actor = await _factory.NewPartnerActorAsync(impersonationTimeoutMinutes: 30);
        var session = await StartAsync(actor);

        var (activeStatus, activeBody) = await GetContactsAsync(session.AccessToken);
        await SetTenantStatusAsync(actor.CustomerTenantId, TenantStatus.Suspended);
        var (suspendedStatus, suspendedBody) = await GetContactsAsync(session.AccessToken);

        // Both halves are reported together, so a red names every half that failed.
        using (new AssertionScope())
        {
            activeStatus.Should().Be(HttpStatusCode.OK,
                because: $"control: the same request is served while the tenant is Active ({activeBody})");
            suspendedStatus.Should().Be(HttpStatusCode.Forbidden, because: $"the tenant is Suspended ({suspendedBody})");
            ReadErrorOrNull(suspendedBody).Should().Be(SuspendedProse,
                because: "the refusal must come from the tenant-status middleware itself, not from another gate");
        }
    }

    // ── Design D1 test 3: no other /management route is served ────────────────

    [Fact]
    public async Task ManagementRoutes_ShouldNeverBeServed_WhenImpersonatedTenantSuspended()
    {
        var actor = await _factory.NewPartnerActorAsync(impersonationTimeoutMinutes: 30);
        var session = await StartAsync(actor);
        await SetTenantStatusAsync(actor.CustomerTenantId, TenantStatus.Suspended);

        var routes = ManagementRoutes()
            .Where(r => !string.Equals($"{r.Method} {r.Template}", ExemptRoute, StringComparison.Ordinal))
            .ToList();
        routes.Should().HaveCountGreaterThan(10, because: "the route table must actually be enumerated");

        var served = new List<string>();
        foreach (var (method, template) in routes)
        {
            var (status, body) = await SendAsync(new HttpMethod(method), ConcretePath(template), session.AccessToken);
            if ((int)status is >= 200 and < 300)
                served.Add($"{method} {template} -> {(int)status} {body}");
        }

        served.Should().BeEmpty(
            because: "with its tenant suspended, an impersonation token reaches no /management route but ending itself");
        (await _factory.SessionStatusAsync(session.SessionId)).Should().Be(ImpersonationSessionStatus.Active,
            because: "nothing the sweep called may have closed the session");
    }

    // ── Design D1 test 4: the exemption never starts a session ────────────────

    [Fact]
    public async Task StartImpersonation_ShouldBeRefusedAndOpenNoSession_WhenCalledWithTokenIntoSuspendedTenant()
    {
        var actor = await _factory.NewPartnerActorAsync(impersonationTimeoutMinutes: 30);
        var session = await StartAsync(actor);
        await SetTenantStatusAsync(actor.CustomerTenantId, TenantStatus.Suspended);
        var countBefore = await ActiveSessionCountAsync(actor);

        foreach (var path in new[] { "/api/v1/management/impersonate", "/api/v1.0/management/impersonate" })
        {
            using var client = BearerClient(session.AccessToken);
            using var response = await client.PostAsJsonAsync(
                path, new { targetTenantId = actor.CustomerTenantId, readOnly = false });

            ((int)response.StatusCode).Should().BeInRange(400, 499, because: $"POST {path} must be refused");
        }

        (await ActiveSessionCountAsync(actor)).Should().Be(countBefore,
            because: "a refused start opens no session");
    }

    // ── Design D1 test 6: exactly one endpoint carries the marker ─────────────

    [Fact]
    public void RouteTable_ShouldCarryServedWhateverTenantStatusOnExactlyEndImpersonation()
    {
        var marked = Endpoints()
            .Where(e => e.Metadata.GetMetadata<ServedWhateverTenantStatusMetadata>() is not null)
            .SelectMany(e => Methods(e).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .ToList();

        marked.Should().Equal([ExemptRoute],
            because: "ending an impersonation is the only route served whatever the tenant's status (design D1)");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.CloseSessionsAsync(_startedSessions);

    private async Task<StartedImpersonation> StartAsync(PartnerActor actor)
    {
        var session = await _factory.StartImpersonationAsync(actor.Admin, actor.CustomerTenantId);
        _startedSessions.Add(session.SessionId);
        return session;
    }

    private async Task LimitConcurrentSessionsAsync(PartnerActor actor, int maxConcurrent)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITenantAuthConfigStore>().SaveAsync(
            new TenantAuthConfig
            {
                TenantId = actor.Admin.TenantId.Value,
                ImpersonationAutoTimeoutMinutes = 30,
                ImpersonationMaxConcurrentSessions = maxConcurrent,
            },
            CancellationToken.None);
    }

    private Task<int> ActiveSessionCountAsync(PartnerActor actor) =>
        _factory.Services.GetRequiredService<IImpersonationSessionStore>()
            .CountActiveByActorTenantAsync(actor.Admin.TenantId.Value, CancellationToken.None);

    private async Task SetTenantStatusAsync(string tenantId, TenantStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var tenant = await store.GetAsync(tenantId, CancellationToken.None);
        tenant.Should().NotBeNull();
        tenant!.Status = status;
        await store.UpsertAsync(tenant);
    }

    private Task<(HttpStatusCode Status, string Body)> GetContactsAsync(string bearer) =>
        SendAsync(HttpMethod.Get, "/api/v1/contacts", bearer);

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, string bearer)
    {
        using var client = BearerClient(bearer);
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch)
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private HttpClient BearerClient(string bearer)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private List<RouteEndpoint> Endpoints()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider
            .GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    private static IEnumerable<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];

    /// <summary>Every <c>/management</c> endpoint of the route table, as method and route template.</summary>
    private List<(string Method, string Template)> ManagementRoutes() =>
        Endpoints()
            .Where(e => e.RoutePattern.RawText is { } t
                && (t.Contains("/management/", StringComparison.Ordinal) || t.EndsWith("/management", StringComparison.Ordinal)))
            .SelectMany(e => Methods(e).Select(m => (Method: m, Template: e.RoutePattern.RawText!)))
            .Where(r => r.Method != "*" && r.Method != "OPTIONS" && r.Method != "HEAD")
            .Distinct()
            .OrderBy(r => r.Template, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal)
            .ToList();

    /// <summary>A request path for <paramref name="template"/>: version 1, every other parameter a probe value.</summary>
    private static string ConcretePath(string template)
    {
        var path = template.Replace("{version:apiVersion}", "1", StringComparison.Ordinal);
        return ParameterPattern().Replace(path, "probe");
    }

    /// <summary>The <c>error</c> field of a JSON error body, or null when the body carries none.</summary>
    private static string? ReadErrorOrNull(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\{[^}]+\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ParameterPattern();
}
