using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// ADR-0027 — locks the RequireOperationalTenant contract: operational
/// endpoint groups reject Platform and Partner callers with HTTP 409 +
/// a TenantTypeMismatchProblem body. NEUTRAL endpoints (admin/users,
/// admin/audit, etc.) continue to serve any tenant type. Impersonation
/// from a Platform Admin into a Customer flips the resolved tenant so
/// the gate passes naturally.
/// </summary>
public sealed class TenantTypeGateTests :
    IClassFixture<PlatformTenantAuthenticatedApiFactory>,
    IClassFixture<PartnerApiFactory>,
    IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _platformCaller;
    private readonly HttpClient _partnerCaller;
    private readonly HttpClient _customerCaller;

    public TenantTypeGateTests(
        PlatformTenantAuthenticatedApiFactory platformFactory,
        PartnerApiFactory partnerFactory,
        AuthenticatedPlatformApiFactory customerFactory)
    {
        _platformCaller = platformFactory.CreateAuthenticatedClient();
        _partnerCaller = partnerFactory.CreatePartnerClient();
        _customerCaller = customerFactory.CreateAuthenticatedClient();
    }

    // ─── Representative operational endpoints across the 4 route shapes ─────
    // The gate is wired identically on every group so this matrix locks the
    // contract without enumerating all 29 application sites. Coverage of any
    // specific group's full route set lives in that group's own test class.

    public static TheoryData<string, string, string?> OperationalEndpoints => new()
    {
        // /admin/{operational} sub-group (Phase A.5 split)
        { "POST", "/api/v1/admin/queues", "{\"name\":\"X\"}" },
        { "GET",  "/api/v1/admin/queues", null },
        { "GET",  "/api/v1/admin/agents", null },
        { "GET",  "/api/v1/admin/teams", null },
        // operational sub-routes that previously lived under /admin/* sub-groups
        { "GET",  "/api/v1/admin/channels", null },
        { "GET",  "/api/v1/admin/bots", null },
        { "GET",  "/api/v1/admin/flows", null },
        { "GET",  "/api/v1/admin/surveys", null },
        { "GET",  "/api/v1/admin/skills", null },
        { "GET",  "/api/v1/admin/articles", null },
        // /queues/{queueId}/members (Phase A.6 RESTful nested route)
        { "GET",  "/api/v1/queues/abc/members", null },
        { "POST", "/api/v1/queues/abc/members", "{\"agentId\":\"a\"}" },
        // Per-user / per-conversation operational surfaces
        { "GET",  "/api/v1/conversations", null },
        { "GET",  "/api/v1/contacts/abc", null },
        { "GET",  "/api/v1/cases", null },
        // Analytics + supervisor surfaces (SupervisorPlus)
        { "GET",  "/api/v1/analytics/dashboard", null },
        { "GET",  "/api/v1/operations/queue-metrics", null },
    };

    [Theory]
    [MemberData(nameof(OperationalEndpoints))]
    public async Task OperationalEndpoint_ShouldReturn409_WhenCallerIsPlatformTenant(
        string method, string path, string? body)
    {
        var response = await SendAsync(_platformCaller, method, path, body);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: $"the gate rejects {method} {path} for callers in a Platform tenant");

        var problem = await ParseProblemAsync(response);
        problem["type"]!.GetValue<string>()
            .Should().Be("https://verbara.platform/errors/tenant-type-mismatch");
        problem["tenantType"]!.GetValue<string>().Should().Be(nameof(TenantType.Platform));
        problem["expectedType"]!.GetValue<string>().Should().Be(nameof(TenantType.Customer));
        problem["status"]!.GetValue<int>().Should().Be(409);
    }

    [Theory]
    [MemberData(nameof(OperationalEndpoints))]
    public async Task OperationalEndpoint_ShouldReturn409_WhenCallerIsPartnerTenant(
        string method, string path, string? body)
    {
        var response = await SendAsync(_partnerCaller, method, path, body);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: $"the gate rejects {method} {path} for callers in a Partner tenant");

        var problem = await ParseProblemAsync(response);
        problem["tenantType"]!.GetValue<string>().Should().Be(nameof(TenantType.Partner));
    }

    [Theory]
    [MemberData(nameof(OperationalEndpoints))]
    public async Task OperationalEndpoint_ShouldPassGate_WhenCallerIsCustomerTenant(
        string method, string path, string? body)
    {
        var response = await SendAsync(_customerCaller, method, path, body);

        // A Customer caller passes the tenant-type gate. The actual status
        // code is determined by the endpoint's own logic (e.g. 404 for an
        // unknown queue/contact ID, 400 for missing body fields, 200/201/204
        // for happy paths) — what matters here is that we DON'T see 409.
        response.StatusCode.Should().NotBe(HttpStatusCode.Conflict,
            because: $"Customer callers must reach the endpoint logic for {method} {path}");
    }

    // ─── NEUTRAL endpoints — gate does NOT apply ────────────────────────────

    [Fact]
    public async Task NeutralEndpoint_AdminUsers_ShouldReturn200_WhenCallerIsPlatformTenant()
    {
        // /admin/users is NEUTRAL — every tenant manages its own user directory.
        // Platform Admin must be able to list its own users without 409.
        var response = await _platformCaller.GetAsync("/api/v1/admin/users");

        response.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(JsonNode.Parse(body));
        }
        return await client.SendAsync(request);
    }

    private static async Task<JsonNode> ParseProblemAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(raw);
        if (node is null) throw new InvalidOperationException($"Response body was not JSON: {raw}");
        return node;
    }
}
