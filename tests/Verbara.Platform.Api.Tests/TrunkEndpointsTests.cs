using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Phase 2.4 — locks the <c>MatchHost</c> (IP-ACL) field round-trip through the trunk admin
/// surface (<c>/api/v1/admin/trunks</c>). The field flows Platform DTO → Pro <c>Trunk</c> →
/// store; the actual <c>ps_endpoint_id_ips</c> write is covered by the Pro engine + integration
/// tests. Uses the Customer-typed test tenant so <c>RequireOperationalTenant</c> passes.
/// </summary>
public sealed class TrunkEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _admin;

    public TrunkEndpointsTests(AuthenticatedPlatformApiFactory adminFactory)
    {
        _admin = adminFactory.CreateAuthenticatedClient();
    }

    private static object TrunkBody(string name, string? matchHost) => new
    {
        name,
        displayName = (string?)null,
        type = "pjsip",
        isActive = true,
        maxChannels = 10,
        transport = "transport-udp",
        codecs = "ulaw,alaw",
        authUsername = (string?)null,
        authPassword = (string?)null,
        registrationUri = (string?)null,
        clientUri = (string?)null,
        context = "from-trunk",
        matchHost,
    };

    [Fact]
    public async Task CreateTrunk_ShouldPersistMatchHost_WhenProvided()
    {
        var create = await _admin.PostAsJsonAsync(
            "/api/v1/admin/trunks", TrunkBody("carrier-ipacl", "203.0.113.0/24"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = JsonNode.Parse(await create.Content.ReadAsStringAsync());
        dto!["matchHost"]!.GetValue<string>().Should().Be("203.0.113.0/24");

        var id = dto!["id"]!.GetValue<long>();
        var get = await _admin.GetAsync($"/api/v1/admin/trunks/{id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await get.Content.ReadAsStringAsync())!["matchHost"]!.GetValue<string>()
            .Should().Be("203.0.113.0/24");
    }

    [Fact]
    public async Task CreateTrunk_ShouldDefaultMatchHostToNull_WhenOmitted()
    {
        var create = await _admin.PostAsJsonAsync(
            "/api/v1/admin/trunks", TrunkBody("carrier-no-ipacl", matchHost: null));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        // A null MatchHost serializes as absent / JSON-null, so the node indexer yields C# null.
        JsonNode.Parse(await create.Content.ReadAsStringAsync())!["matchHost"].Should().BeNull();
    }

    [Fact]
    public async Task UpdateTrunk_ShouldSetMatchHost_WhenValidProvided()
    {
        var create = await _admin.PostAsJsonAsync(
            "/api/v1/admin/trunks", TrunkBody("carrier-update", matchHost: null));
        var id = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();

        var set = await _admin.PutAsJsonAsync(
            $"/api/v1/admin/trunks/{id}", new { matchHost = "198.51.100.7" });
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await set.Content.ReadAsStringAsync())!["matchHost"]!.GetValue<string>()
            .Should().Be("198.51.100.7");
    }

    [Fact]
    public async Task UpdateTrunk_ShouldClearMatchHost_WhenEmptyStringSent()
    {
        // Empty-string is the explicit IP-ACL clear sentinel → stored null (engine deletes the
        // identify row). This is the security-relevant removal path.
        var create = await _admin.PostAsJsonAsync(
            "/api/v1/admin/trunks", TrunkBody("carrier-clear", "203.0.113.0/24"));
        var id = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();

        var clear = await _admin.PutAsJsonAsync($"/api/v1/admin/trunks/{id}", new { matchHost = "" });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await clear.Content.ReadAsStringAsync())!["matchHost"].Should().BeNull();

        var get = await _admin.GetAsync($"/api/v1/admin/trunks/{id}");
        JsonNode.Parse(await get.Content.ReadAsStringAsync())!["matchHost"].Should().BeNull();
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.0/99")]
    [InlineData("999.0.0.1")]
    public async Task CreateTrunk_ShouldReturn400_WhenMatchHostMalformed(string badMatchHost)
    {
        var response = await _admin.PostAsJsonAsync(
            "/api/v1/admin/trunks", TrunkBody("carrier-badcidr", badMatchHost));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
