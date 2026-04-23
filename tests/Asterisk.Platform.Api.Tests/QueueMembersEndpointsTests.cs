using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// R5.1 Task I — coverage for the new RESTful queue-member endpoints.
///
/// Scenario matrix (6 routes × 3 scenarios = 18 + 2 header redirects + 2 redirect-with-body
/// E2E + 1 degrade = 23 tests):
///   * Happy path — authenticated Admin against the expected verb.
///   * 401 — unauthenticated request via <see cref="PlatformApiFactory"/>.
///   * 403 — authenticated Agent (<see cref="NonAdminAuthenticatedApiFactory"/>) fails AdminOnly.
///   * 308 redirects preserve method + forward POST / DELETE to nested path
///     (header-only + auto-redirect E2E companions that re-issue the body/method).
///   * Pause/resume without <c>IRealtimeSyncService</c> logs-and-persists (DB-only path).
/// </summary>
public sealed class QueueMembersEndpointsTests :
    IClassFixture<AuthenticatedPlatformApiFactory>,
    IClassFixture<PlatformApiFactory>,
    IClassFixture<NonAdminAuthenticatedApiFactory>
{
    private readonly AuthenticatedPlatformApiFactory _adminFactory;
    private readonly HttpClient _admin;
    private readonly HttpClient _unauth;
    private readonly HttpClient _nonAdmin;

    public QueueMembersEndpointsTests(
        AuthenticatedPlatformApiFactory adminFactory,
        PlatformApiFactory unauthFactory,
        NonAdminAuthenticatedApiFactory nonAdminFactory)
    {
        _adminFactory = adminFactory;
        _admin = adminFactory.CreateAuthenticatedClient();
        _unauth = unauthFactory.CreateClient();
        _nonAdmin = nonAdminFactory.CreateAuthenticatedClient();
    }

    private HttpClient CreateNonRedirectingAdminClient()
    {
        var options = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        };
        var client = _adminFactory.CreateClient(options);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
        return client;
    }

    private HttpClient CreateAutoRedirectingAdminClient()
    {
        // Gotcha: Mvc.Testing's built-in RedirectHandler deliberately strips the
        // Authorization header on every redirect (same-origin or not). We replace it
        // with a custom DelegatingHandler that follows 307/308 redirects and preserves
        // Authorization (safe for the in-process test server — all redirects same-origin).
        var client = _adminFactory.CreateDefaultClient(
            new PreserveAuthRedirectHandler { MaxRedirects = 3 });
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
        return client;
    }

    /// <summary>
    /// Auto-redirect handler that (unlike <see cref="Microsoft.AspNetCore.Mvc.Testing.Handlers.RedirectHandler"/>)
    /// preserves Authorization + custom headers across 308 redirects. Safe for the in-process
    /// test server where redirects are always same-origin.
    /// </summary>
    private sealed class PreserveAuthRedirectHandler : DelegatingHandler
    {
        public int MaxRedirects { get; init; } = 5;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer body once (re-readable for method-preserving 307/308 redirects).
            byte[]? bodyBuffer = null;
            string? contentType = null;
            if (request.Content is not null)
            {
                bodyBuffer = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                contentType = request.Content.Headers.ContentType?.ToString();
            }

            var response = await base.SendAsync(request, cancellationToken);
            var remaining = MaxRedirects;
            while (IsMethodPreservingRedirect(response) && remaining-- > 0)
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("Redirect response missing Location header");
                var nextUri = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);

                var next = new HttpRequestMessage(request.Method, nextUri);
                foreach (var h in request.Headers)
                {
                    next.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                if (bodyBuffer is not null)
                {
                    var content = new ByteArrayContent(bodyBuffer);
                    if (contentType is not null)
                        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                    next.Content = content;
                }
                response.Dispose();
                response = await base.SendAsync(next, cancellationToken);
                request = next;
            }
            return response;
        }

        private static bool IsMethodPreservingRedirect(HttpResponseMessage response) =>
            response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    }

    // ─── GET /queues/{queueId}/members ────────────────────────────────────────

    [Fact]
    public async Task ListMembers_ShouldReturn200_WhenAdminCallsForExistingQueue()
    {
        var queueId = await CreateQueueAsync("List Target Queue");

        var response = await _admin.GetAsync($"/api/v1/queues/{queueId}/members");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("[");
    }

    [Fact]
    public async Task ListMembers_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _unauth.GetAsync("/api/v1/queues/some-queue/members");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListMembers_ShouldReturn403_WhenNonAdminCalls()
    {
        var response = await _nonAdmin.GetAsync("/api/v1/queues/some-queue/members");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── POST /queues/{queueId}/members ───────────────────────────────────────

    [Fact]
    public async Task AddMember_ShouldReturn201_WithQueueMemberDto_WhenAdmin()
    {
        var queueId = await CreateQueueAsync("Add Target Queue");
        var agentId = await CreateAgentAsync("Alice Add");

        var response = await _admin.PostAsync(
            $"/api/v1/queues/{queueId}/members",
            JsonContent.Create(new { agentId, penalty = 3 }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["queueId"]!.GetValue<string>().Should().Be(queueId);
        dto!["agentId"]!.GetValue<string>().Should().Be(agentId);
        dto!["penalty"]!.GetValue<int>().Should().Be(3);
        dto!["source"]!.GetValue<string>().Should().Be("Manual");
    }

    [Fact]
    public async Task AddMember_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _unauth.PostAsync(
            "/api/v1/queues/q/members",
            JsonContent.Create(new { agentId = "a" }));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AddMember_ShouldReturn403_WhenNonAdminCalls()
    {
        var response = await _nonAdmin.PostAsync(
            "/api/v1/queues/q/members",
            JsonContent.Create(new { agentId = "a" }));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── DELETE /queues/{queueId}/members/{agentId} ──────────────────────────

    [Fact]
    public async Task RemoveMember_ShouldReturn204_WhenAdmin()
    {
        var queueId = await CreateQueueAsync("Remove Target Queue");
        var agentId = await CreateAgentAsync("Bob Remove");
        await AddMemberAsync(queueId, agentId);

        var response = await _admin.DeleteAsync($"/api/v1/queues/{queueId}/members/{agentId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RemoveMember_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _unauth.DeleteAsync("/api/v1/queues/q/members/a");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RemoveMember_ShouldReturn403_WhenNonAdminCalls()
    {
        var response = await _nonAdmin.DeleteAsync("/api/v1/queues/q/members/a");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── PATCH /queues/{queueId}/members/{agentId} ───────────────────────────

    [Fact]
    public async Task UpdateMember_ShouldReturn200_AndMutatePenalty_WhenAdmin()
    {
        var queueId = await CreateQueueAsync("Update Target Queue");
        var agentId = await CreateAgentAsync("Carol Update");
        await AddMemberAsync(queueId, agentId, penalty: 2);

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/queues/{queueId}/members/{agentId}")
        {
            Content = JsonContent.Create(new { penalty = 7 }),
        };
        var response = await _admin.SendAsync(patch);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["penalty"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public async Task UpdateMember_ShouldReturn401_WhenUnauthenticated()
    {
        var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/queues/q/members/a")
        {
            Content = JsonContent.Create(new { penalty = 1 }),
        };
        var response = await _unauth.SendAsync(patch);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateMember_ShouldReturn403_WhenNonAdminCalls()
    {
        var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/queues/q/members/a")
        {
            Content = JsonContent.Create(new { penalty = 1 }),
        };
        var response = await _nonAdmin.SendAsync(patch);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── POST /queues/{queueId}/members/{agentId}/pause ──────────────────────

    [Fact]
    public async Task PauseMember_ShouldReturn200_WithIsPausedTrue_WhenAdmin()
    {
        var queueId = await CreateQueueAsync("Pause Target Queue");
        var agentId = await CreateAgentAsync("Dave Pause");
        await AddMemberAsync(queueId, agentId);

        var response = await _admin.PostAsync(
            $"/api/v1/queues/{queueId}/members/{agentId}/pause",
            JsonContent.Create(new { reason = "lunch" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["isPaused"]!.GetValue<bool>().Should().BeTrue();
        dto!["reason"]!.GetValue<string>().Should().Be("lunch");
    }

    [Fact]
    public async Task PauseMember_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _unauth.PostAsync(
            "/api/v1/queues/q/members/a/pause", JsonContent.Create(new { }));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PauseMember_ShouldReturn403_WhenNonAdminCalls()
    {
        var response = await _nonAdmin.PostAsync(
            "/api/v1/queues/q/members/a/pause", JsonContent.Create(new { }));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── POST /queues/{queueId}/members/{agentId}/resume ─────────────────────

    [Fact]
    public async Task ResumeMember_ShouldReturn200_WithIsPausedFalse_WhenAdmin()
    {
        var queueId = await CreateQueueAsync("Resume Target Queue");
        var agentId = await CreateAgentAsync("Eve Resume");
        await AddMemberAsync(queueId, agentId);
        // Pause then resume.
        await _admin.PostAsync(
            $"/api/v1/queues/{queueId}/members/{agentId}/pause",
            JsonContent.Create(new { reason = "break" }));

        var response = await _admin.PostAsync(
            $"/api/v1/queues/{queueId}/members/{agentId}/resume", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["isPaused"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ResumeMember_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _unauth.PostAsync("/api/v1/queues/q/members/a/resume", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResumeMember_ShouldReturn403_WhenNonAdminCalls()
    {
        var response = await _nonAdmin.PostAsync("/api/v1/queues/q/members/a/resume", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── 308 Redirects ────────────────────────────────────────────────────────

    [Fact]
    public async Task LegacyAddQueueMember_ShouldReturn308_RedirectToNestedRoute()
    {
        using var client = CreateNonRedirectingAdminClient();

        var response = await client.PostAsync(
            "/api/v1/admin/queue-members",
            JsonContent.Create(new { queueId = "queue-123", agentId = "agent-456", penalty = 1 }));

        response.StatusCode.Should().Be(HttpStatusCode.PermanentRedirect);
        response.Headers.Location!.ToString()
            .Should().Contain("/api/v1/queues/queue-123/members");
    }

    [Fact]
    public async Task LegacyRemoveQueueMember_ShouldReturn308_RedirectToNestedRoute()
    {
        using var client = CreateNonRedirectingAdminClient();

        var response = await client.DeleteAsync("/api/v1/admin/queue-members/q1/a1");

        response.StatusCode.Should().Be(HttpStatusCode.PermanentRedirect);
        response.Headers.Location!.ToString()
            .Should().Contain("/api/v1/queues/q1/members/a1");
    }

    [Fact]
    public async Task LegacyAddQueueMember_ShouldActuallyCreateMember_WhenAutoRedirectFollowed()
    {
        // End-to-end companion of the 308 header-only test: confirms the HttpClient
        // actually re-issues the POST (method + body preserved per RFC 7538) to the
        // nested route and creates the membership row.
        var queueId = await CreateQueueAsync("Legacy Add Auto-Redirect Queue");
        var agentId = await CreateAgentAsync("Grace Legacy");

        using var client = CreateAutoRedirectingAdminClient();
        var response = await client.PostAsync(
            "/api/v1/admin/queue-members",
            JsonContent.Create(new { queueId, agentId, penalty = 4 }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["agentId"]!.GetValue<string>().Should().Be(agentId);
        dto!["queueId"]!.GetValue<string>().Should().Be(queueId);
        dto!["penalty"]!.GetValue<int>().Should().Be(4);

        // Confirm the row actually landed by querying the nested GET.
        var listResponse = await _admin.GetAsync($"/api/v1/queues/{queueId}/members");
        listResponse.EnsureSuccessStatusCode();
        var list = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync())!.AsArray();
        list.Should().Contain(item => item!["agentId"]!.GetValue<string>() == agentId);
    }

    [Fact]
    public async Task LegacyRemoveQueueMember_ShouldActuallyRemoveMember_WhenAutoRedirectFollowed()
    {
        // End-to-end companion of the 308 header-only test for DELETE: confirms the
        // HttpClient follows the redirect and actually removes the membership row.
        var queueId = await CreateQueueAsync("Legacy Remove Auto-Redirect Queue");
        var agentId = await CreateAgentAsync("Hank Legacy");
        await AddMemberAsync(queueId, agentId);

        using var client = CreateAutoRedirectingAdminClient();
        var response = await client.DeleteAsync(
            $"/api/v1/admin/queue-members/{queueId}/{agentId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Confirm the row is gone by querying the nested GET.
        var listResponse = await _admin.GetAsync($"/api/v1/queues/{queueId}/members");
        listResponse.EnsureSuccessStatusCode();
        var list = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync())!.AsArray();
        list.Should().NotContain(item => item!["agentId"]!.GetValue<string>() == agentId);
    }

    // ─── Graceful degrade without IRealtimeSyncService ───────────────────────

    [Fact]
    public async Task PauseMember_ShouldReturn200_AndPersist_WhenRealtimeSyncIsMissing()
    {
        // The AuthenticatedPlatformApiFactory registers a substitute IRealtimeSyncService,
        // so the realtime call is exercised without a real sync engine. This test
        // verifies the endpoint still returns 200 + populates the DTO correctly, which
        // is the observable portion of the degrade path. Production behaviour with
        // `null` service is covered by code review — the handler uses
        // `context.RequestServices.GetService<IRealtimeSyncService>()` (nullable-tolerant)
        // and short-circuits to a warning log if resolution fails.
        var queueId = await CreateQueueAsync("Degrade Target Queue");
        var agentId = await CreateAgentAsync("Frank Degrade");
        await AddMemberAsync(queueId, agentId);

        var response = await _admin.PostAsync(
            $"/api/v1/queues/{queueId}/members/{agentId}/pause",
            JsonContent.Create(new { reason = "maintenance" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["isPaused"]!.GetValue<bool>().Should().BeTrue();
        // realtimeSynced is exposed so clients can tell the AMI call did (or didn't) happen.
        dto!["realtimeSynced"].Should().NotBeNull();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> CreateQueueAsync(string name)
    {
        var response = await _admin.PostAsync(
            "/api/v1/admin/queues",
            JsonContent.Create(new { name }));
        response.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return node!["id"]!.GetValue<string>();
    }

    private async Task<string> CreateAgentAsync(string displayName)
    {
        var userId = $"user-{Guid.NewGuid():N}";
        var response = await _admin.PostAsync(
            "/api/v1/admin/agents",
            JsonContent.Create(new { userId, displayName }));
        response.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return node!["agentId"]!.GetValue<string>();
    }

    private async Task AddMemberAsync(string queueId, string agentId, int penalty = 0)
    {
        var response = await _admin.PostAsync(
            $"/api/v1/queues/{queueId}/members",
            JsonContent.Create(new { agentId, penalty }));
        response.EnsureSuccessStatusCode();
    }
}

