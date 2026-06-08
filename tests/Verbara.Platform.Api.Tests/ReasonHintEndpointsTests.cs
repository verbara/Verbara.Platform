using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// C10 — contract coverage for the reason-hint admin endpoints
/// (<c>/api/v1/admin/reason-hints</c>): happy-path CRUD lifecycle, create
/// validation (missing ScopeRef → 400, unknown Scope → 400), the
/// AdvancedTypification license gate (402 when unlicensed) and the AdminOnly
/// RBAC gate (403 for a non-admin caller). Mirrors
/// <see cref="DidRouteEndpointsTests"/> and the typification 402 pattern.
/// </summary>
public sealed class ReasonHintEndpointsTests :
    IClassFixture<AuthenticatedPlatformApiFactory>,
    IClassFixture<NonAdminAuthenticatedApiFactory>
{
    private readonly HttpClient _admin;
    private readonly HttpClient _nonAdmin;

    public ReasonHintEndpointsTests(
        AuthenticatedPlatformApiFactory adminFactory,
        NonAdminAuthenticatedApiFactory nonAdminFactory)
    {
        _admin = adminFactory.CreateAuthenticatedClient();
        _nonAdmin = nonAdminFactory.CreateAuthenticatedClient();
    }

    private static string UniqueScopeRef() =>
        "+1" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static object ValidBody(string scopeRef, string scope = "Did") => new
    {
        scope,
        scopeRef,
        reasonPath = "[\"CITAS\",\"REPROG\"]",
        priority = 10,
        isActive = true,
    };

    [Fact]
    public async Task CreateReasonHint_ShouldReturn201_WhenValid()
    {
        var scopeRef = UniqueScopeRef();

        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/reason-hints", ValidBody(scopeRef));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["id"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        dto!["scope"]!.GetValue<string>().Should().Be("Did");
        dto!["scopeRef"]!.GetValue<string>().Should().Be(scopeRef);
        dto!["reasonPath"]!.GetValue<string>().Should().Be("[\"CITAS\",\"REPROG\"]");
        dto!["priority"]!.GetValue<int>().Should().Be(10);
        dto!["isActive"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CreateReasonHint_ShouldReturn400_WhenScopeRefMissing()
    {
        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/reason-hints",
            new { scope = "Did", scopeRef = "", reasonPath = "[\"X\"]", priority = 0, isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateReasonHint_ShouldReturn400_WhenScopeInvalid()
    {
        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/reason-hints",
            new { scope = "Galaxy", scopeRef = UniqueScopeRef(), reasonPath = "[\"X\"]", priority = 0, isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateReasonHint_ShouldReturn400_WhenReasonPathNotJsonArray()
    {
        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/reason-hints",
            new { scope = "Did", scopeRef = UniqueScopeRef(), reasonPath = "CITAS", priority = 0, isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateReasonHint_ShouldReturn201_WhenReasonPathIsValidJsonCodesArray()
    {
        var scopeRef = UniqueScopeRef();

        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/reason-hints",
            new { scope = "Did", scopeRef, reasonPath = "[\"CITAS\",\"REPROG\"]", priority = 5, isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["reasonPath"]!.GetValue<string>().Should().Be("[\"CITAS\",\"REPROG\"]");
    }

    [Fact]
    public async Task ListReasonHints_ShouldReturn402_WhenAdvancedTypificationUnlicensed()
    {
        using var factory = new NoTypificationLicenseFactory();
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/admin/reason-hints");

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task ReasonHintEndpoints_ShouldReturn403_WhenNonAdminCaller()
    {
        var response = await _nonAdmin.GetAsync("/api/v1/admin/reason-hints");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutReasonHint_ShouldUpdate_WhenExists()
    {
        var created = await _admin.PostAsJsonAsync(
            "/api/v1/admin/reason-hints", ValidBody(UniqueScopeRef()));
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var response = await _admin.PutAsJsonAsync(
            $"/api/v1/admin/reason-hints/{id}",
            new { reasonPath = "[\"FACTURACION\"]", priority = 99, isActive = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        dto!["reasonPath"]!.GetValue<string>().Should().Be("[\"FACTURACION\"]");
        dto!["priority"]!.GetValue<int>().Should().Be(99);
        dto!["isActive"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task PutReasonHint_ShouldReturn404_WhenMissing()
    {
        var response = await _admin.PutAsJsonAsync(
            "/api/v1/admin/reason-hints/missing-id",
            new { priority = 1 });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteReasonHint_ShouldRemove_WhenExists()
    {
        var created = await _admin.PostAsJsonAsync(
            "/api/v1/admin/reason-hints", ValidBody(UniqueScopeRef()));
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var response = await _admin.DeleteAsync($"/api/v1/admin/reason-hints/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var followUp = await _admin.GetAsync($"/api/v1/admin/reason-hints/{id}");
        followUp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteReasonHint_ShouldReturn404_WhenMissing()
    {
        var response = await _admin.DeleteAsync("/api/v1/admin/reason-hints/missing-id");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
