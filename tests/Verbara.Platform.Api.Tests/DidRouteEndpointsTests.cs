using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Phase 2.1 — contract coverage for the DID-route admin endpoints
/// (<c>/api/v1/admin/did-routes</c>). Asserts the happy-path CRUD lifecycle,
/// the duplicate-DID 409, auth (401 unauthenticated / 403 non-admin), and that
/// the operational-tenant gate lets a Customer-typed tenant through. Uses
/// <see cref="AuthenticatedPlatformApiFactory"/> whose test tenant is seeded as
/// <c>TenantType.Customer</c> so <c>RequireOperationalTenant</c> passes.
/// </summary>
public sealed class DidRouteEndpointsTests :
    IClassFixture<AuthenticatedPlatformApiFactory>,
    IClassFixture<PlatformApiFactory>,
    IClassFixture<NonAdminAuthenticatedApiFactory>
{
    private readonly HttpClient _admin;
    private readonly HttpClient _unauth;
    private readonly HttpClient _nonAdmin;

    public DidRouteEndpointsTests(
        AuthenticatedPlatformApiFactory adminFactory,
        PlatformApiFactory unauthFactory,
        NonAdminAuthenticatedApiFactory nonAdminFactory)
    {
        _admin = adminFactory.CreateAuthenticatedClient();
        _unauth = unauthFactory.CreateClient();
        _nonAdmin = nonAdminFactory.CreateAuthenticatedClient();

        // DID-route Create/Update now verify the target queue EXISTS in the
        // tenant. Seed every queue id referenced by the contract tests into the
        // singleton InMemory IQueueStore so the existing happy-path cases keep
        // passing (idempotent: SaveAsync upserts).
        SeedQueues(adminFactory.Services);
    }

    private static readonly string[] s_seededQueueIds =
    [
        "queue-support", "queue-1", "queue-2", "queue-get", "queue-bydid",
        "queue-active", "queue-before", "queue-after", "queue-x", "queue-del",
        "queue-exists",
    ];

    private static void SeedQueues(IServiceProvider services)
    {
        var queueStore = services.GetRequiredService<IQueueStore>();
        var tenantId = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId);
        foreach (var queueId in s_seededQueueIds)
        {
            var queue = new Queue
            {
                QueueId = EntityId.From(queueId),
                TenantId = tenantId,
                Name = queueId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            queueStore.SaveAsync(queue, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static string UniqueDid() => "+1" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Create_ShouldReturn201WithDto_WhenAdminPostsValidRoute()
    {
        var did = UniqueDid();

        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-support", isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["id"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        dto!["did"]!.GetValue<string>().Should().Be(did);
        dto!["queueId"]!.GetValue<string>().Should().Be("queue-support");
        dto!["isActive"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Create_ShouldReturn409_WhenDidAlreadyExistsForTenant()
    {
        var did = UniqueDid();
        var first = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-1", isActive = true });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var duplicate = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-2", isActive = true });

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Get_ShouldReturn200_WhenRouteExists()
    {
        var did = UniqueDid();
        var created = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-get", isActive = true });
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var response = await _admin.GetAsync($"/api/v1/admin/did-routes/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["did"]!.GetValue<string>().Should().Be(did);
    }

    [Fact]
    public async Task Get_ShouldReturn404_WhenRouteMissing()
    {
        var response = await _admin.GetAsync("/api/v1/admin/did-routes/does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetByDid_ShouldReturn200_WhenDidExists()
    {
        var did = UniqueDid();
        await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-bydid", isActive = true });

        var response = await _admin.GetAsync($"/api/v1/admin/did-routes/by-did/{Uri.EscapeDataString(did)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["queueId"]!.GetValue<string>().Should().Be("queue-bydid");
    }

    [Fact]
    public async Task List_ShouldReturn200WithJsonArray_WhenAdminCalls()
    {
        var response = await _admin.GetAsync("/api/v1/admin/did-routes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("[");
    }

    [Fact]
    public async Task ListActive_ShouldOmitInactiveRoutes_WhenRouteDisabled()
    {
        var did = UniqueDid();
        var created = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-active", isActive = false });
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var response = await _admin.GetAsync("/api/v1/admin/did-routes/active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        arr.Select(n => n!["id"]!.GetValue<string>()).Should().NotContain(id);
    }

    [Fact]
    public async Task Update_ShouldReturn200WithUpdatedFields_WhenRouteExists()
    {
        var did = UniqueDid();
        var created = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-before", isActive = true });
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var response = await _admin.PutAsJsonAsync(
            $"/api/v1/admin/did-routes/{id}",
            new { queueId = "queue-after", isActive = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["queueId"]!.GetValue<string>().Should().Be("queue-after");
        dto!["isActive"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Update_ShouldReturn404_WhenRouteMissing()
    {
        var response = await _admin.PutAsJsonAsync(
            "/api/v1/admin/did-routes/missing-id",
            new { queueId = "queue-x" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_ShouldReturn409_WhenRenamingOntoExistingDidForTenant()
    {
        var didA = UniqueDid();
        var didB = UniqueDid();
        await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes", new { did = didA, queueId = "queue-1", isActive = true });
        var createdB = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes", new { did = didB, queueId = "queue-2", isActive = true });
        var idB = JsonNode.Parse(await createdB.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        // Rename route B's DID onto route A's DID — UNIQUE (tenant_id, did) conflict.
        var response = await _admin.PutAsJsonAsync(
            $"/api/v1/admin/did-routes/{idB}", new { did = didA });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delete_ShouldReturn204_WhenRouteExists()
    {
        var did = UniqueDid();
        var created = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-del", isActive = true });
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var response = await _admin.DeleteAsync($"/api/v1/admin/did-routes/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var followUp = await _admin.GetAsync($"/api/v1/admin/did-routes/{id}");
        followUp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ShouldReturn404_WhenRouteMissing()
    {
        var response = await _admin.DeleteAsync("/api/v1/admin/did-routes/missing-id");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Validation hardening (P1): E.164 + queue existence ───────────────────

    [Theory]
    [InlineData("not-a-number")]      // letters
    [InlineData("12345")]             // too short (< 7 digits)
    [InlineData("1234567890123456")]  // too long (> 15 digits)
    [InlineData("0123456789")]        // leading zero
    [InlineData("1800 555 1234")]     // embedded spaces
    [InlineData("+")]                 // bare plus, no digits
    public async Task CreateDidRoute_ShouldReturn400_WhenDidNotE164(string badDid)
    {
        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did = badDid, queueId = "queue-exists", isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateDidRoute_ShouldReturn400_WhenQueueDoesNotExist()
    {
        var did = UniqueDid();

        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-never-seeded", isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["error"]!.GetValue<string>().Should().Contain("queue");
    }

    [Fact]
    public async Task CreateDidRoute_ShouldSucceed_WhenE164AndQueueExists()
    {
        var did = UniqueDid();

        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/did-routes",
            new { did, queueId = "queue-exists", isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["did"]!.GetValue<string>().Should().Be(did);
        dto!["queueId"]!.GetValue<string>().Should().Be("queue-exists");
    }

    [Fact]
    public async Task List_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _unauth.GetAsync("/api/v1/admin/did-routes");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_ShouldReturn403_WhenNonAdminCalls()
    {
        var response = await _nonAdmin.GetAsync("/api/v1/admin/did-routes");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
