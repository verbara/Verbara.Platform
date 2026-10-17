using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;

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
    private readonly AuthenticatedPlatformApiFactory _factory;

    public TrunkEndpointsTests(AuthenticatedPlatformApiFactory adminFactory)
    {
        _factory = adminFactory;
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

    [Fact]
    public async Task CreateTrunk_ShouldReturn400_WhenCodecTokenInvalid()
    {
        var body = new
        {
            name = "carrier-badcodec",
            displayName = (string?)null,
            type = "pjsip",
            isActive = true,
            maxChannels = 10,
            transport = "transport-udp",
            codecs = "ulaw,ulwa",
            authUsername = (string?)null,
            authPassword = (string?)null,
            registrationUri = (string?)null,
            clientUri = (string?)null,
            context = "from-trunk",
            matchHost = (string?)null,
        };

        var response = await _admin.PostAsJsonAsync("/api/v1/admin/trunks", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var invalidCodecs = json["invalidCodecs"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();
        invalidCodecs.Should().ContainSingle().Which.Should().Be("ulwa");
    }

    [Fact]
    public async Task CreateTrunk_ShouldSucceed_WhenCodecsValid()
    {
        var create = await _admin.PostAsJsonAsync(
            "/api/v1/admin/trunks", TrunkBody("carrier-goodcodec", null));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UpdateTrunk_ShouldReturn400_WhenCodecTokenInvalid()
    {
        var create = await _admin.PostAsJsonAsync(
            "/api/v1/admin/trunks", TrunkBody("carrier-update-badcodec", null));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();

        var response = await _admin.PutAsJsonAsync(
            $"/api/v1/admin/trunks/{id}", new { codecs = "ulaw,ulwa" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var invalidCodecs = json["invalidCodecs"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();
        invalidCodecs.Should().ContainSingle().Which.Should().Be("ulwa");
    }
    // ─── #385: a registration trunk needs a well-formed registration ─────────────

    private static object RegistrationBody(string name, string? registrationUri, string? clientUri) => new
    {
        name,
        displayName = default(string),
        type = "pjsip",
        isActive = true,
        maxChannels = 10,
        transport = "transport-udp",
        codecs = "ulaw,alaw",
        authUsername = "acct100",
        authPassword = "secret",
        registrationUri,
        clientUri,
        context = "from-trunk",
        matchHost = default(string),
    };

    private static async Task<(string Error, string[] Details)> ReadErrorAsync(HttpResponseMessage response)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return (json["error"]!.GetValue<string>(),
            json["details"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task CreateTrunk_ShouldReturn201_WhenRegistrationAndClientUrisAreSipUris()
    {
        var create = await _admin.PostAsJsonAsync("/api/v1/admin/trunks",
            RegistrationBody("carrier-reg-ok", "sip:sip.carrier.example", "sips:acct100@sip.carrier.example"));

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = JsonNode.Parse(await create.Content.ReadAsStringAsync())!;
        dto["registrationUri"]!.GetValue<string>().Should().Be("sip:sip.carrier.example");
        dto["clientUri"]!.GetValue<string>().Should().Be("sips:acct100@sip.carrier.example");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTrunk_ShouldReturn400_WhenRegistrationUriIsBlank(string blank)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/admin/trunks",
            RegistrationBody("carrier-reg-blank", blank, "sip:acct100@sip.carrier.example"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Error.Should().Be("registration-uri-empty");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CreateTrunk_ShouldReturn400_WhenRegistrationUriHasNoClientUri(string? clientUri)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/admin/trunks",
            RegistrationBody("carrier-reg-noclient", "sip:sip.carrier.example", clientUri));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Error.Should().Be("client-uri-required");
    }

    [Theory]
    [InlineData("sip.carrier.example", "sip:acct100@sip.carrier.example", "registrationUri")]
    [InlineData("http://sip.carrier.example", "sip:acct100@sip.carrier.example", "registrationUri")]
    [InlineData("sip:", "sip:acct100@sip.carrier.example", "registrationUri")]
    [InlineData("sip:sip.carrier.example", "acct100@sip.carrier.example", "clientUri")]
    [InlineData("sip:sip.carrier.example", "sip:acct 100@sip.carrier.example", "clientUri")]
    public async Task CreateTrunk_ShouldReturn400_WhenAUriIsNotASipUri(
        string registrationUri, string clientUri, string field)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/admin/trunks",
            RegistrationBody("carrier-reg-notsip", registrationUri, clientUri));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var (error, details) = await ReadErrorAsync(response);
        error.Should().Be("invalid-sip-uri");
        details.Should().Equal(field);
    }

    [Fact]
    public async Task UpdateTrunk_ShouldReturn400_WhenRegistrationUriIsBlank()
    {
        var id = await CreateRegistrationTrunkAsync("carrier-upd-blank");

        var response = await _admin.PutAsJsonAsync($"/api/v1/admin/trunks/{id}", new { registrationUri = " " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Error.Should().Be("registration-uri-empty");
    }

    [Fact]
    public async Task UpdateTrunk_ShouldReturn400_WhenRegistrationUriAddedToATrunkWithoutClientUri()
    {
        var create = await _admin.PostAsJsonAsync("/api/v1/admin/trunks", TrunkBody("carrier-upd-noclient", null));
        var id = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();

        var response = await _admin.PutAsJsonAsync(
            $"/api/v1/admin/trunks/{id}", new { registrationUri = "sip:sip.carrier.example" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Error.Should().Be("client-uri-required");
    }

    [Fact]
    public async Task UpdateTrunk_ShouldReturn400_WhenClientUriClearedWhileRegistrationStays()
    {
        var id = await CreateRegistrationTrunkAsync("carrier-upd-clearclient");

        var response = await _admin.PutAsJsonAsync($"/api/v1/admin/trunks/{id}", new { clientUri = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Error.Should().Be("client-uri-required");
    }

    [Theory]
    [InlineData("carrier.example", null, "registrationUri")]
    [InlineData(null, "acct100@carrier.example", "clientUri")]
    public async Task UpdateTrunk_ShouldReturn400_WhenAUriIsNotASipUri(
        string? registrationUri, string? clientUri, string field)
    {
        var id = await CreateRegistrationTrunkAsync($"carrier-upd-notsip-{field}");

        var response = await _admin.PutAsJsonAsync(
            $"/api/v1/admin/trunks/{id}", new { registrationUri, clientUri });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var (error, details) = await ReadErrorAsync(response);
        error.Should().Be("invalid-sip-uri");
        details.Should().Equal(field);
    }

    [Fact]
    public async Task UpdateTrunk_ShouldReturn200_WhenRegistrationUriChangesToAnotherSipUri()
    {
        var id = await CreateRegistrationTrunkAsync("carrier-upd-ok");

        var response = await _admin.PutAsJsonAsync(
            $"/api/v1/admin/trunks/{id}", new { registrationUri = "sip:backup.carrier.example:5070" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["registrationUri"]!.GetValue<string>()
            .Should().Be("sip:backup.carrier.example:5070");
    }

    [Fact]
    public async Task UpdateTrunk_ShouldReturn200_WhenRequestCarriesNoRegistrationField()
    {
        // Only the registration fields a request carries are validated, so a trunk stored before
        // the rule (registration URI without client URI) stays editable for its other fields.
        var create = await _admin.PostAsJsonAsync("/api/v1/admin/trunks", TrunkBody("carrier-upd-legacy", null));
        var id = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();

        var response = await _admin.PutAsJsonAsync($"/api/v1/admin/trunks/{id}", new { maxChannels = 20 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("sip:sip.carrier.example", null)]
    [InlineData("sip.carrier.example", "sip:acct100@sip.carrier.example")]
    public async Task UpdateTrunk_ShouldReturn200_WhenRequestEchoesTheStoredRegistrationOfALegacyTrunk(
        string storedRegistrationUri, string? storedClientUri)
    {
        // A trunk stored before the rule (no client URI, or a URI without sip:) is seeded straight into the
        // store. The console's edit form sends both stored URIs back on every save; echoing them unchanged
        // must not block an edit of another field.
        var store = _factory.Services.GetRequiredService<TrunkStoreBase>();
        var id = await store.CreateAsync(new Trunk
        {
            Name = $"carrier-legacy-{Guid.NewGuid():N}"[..24],
            Type = TrunkType.Pjsip,
            IsActive = true,
            MaxChannels = 10,
            Transport = "transport-udp",
            Codecs = "ulaw,alaw",
            RegistrationUri = storedRegistrationUri,
            ClientUri = storedClientUri,
            Context = "from-trunk",
        }, AuthenticatedPlatformApiFactory.TestTenantId);

        var response = await _admin.PutAsJsonAsync($"/api/v1/admin/trunks/{id}",
            new { maxChannels = 20, registrationUri = storedRegistrationUri, clientUri = storedClientUri });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateTrunk_ShouldReturn400_WhenLegacyTrunkChangesItsRegistrationUriWithoutClientUri()
    {
        // Changing the URI is a new request: it is validated, client URI included.
        var store = _factory.Services.GetRequiredService<TrunkStoreBase>();
        var id = await store.CreateAsync(new Trunk
        {
            Name = $"carrier-legacy-{Guid.NewGuid():N}"[..24],
            Type = TrunkType.Pjsip,
            IsActive = true,
            MaxChannels = 10,
            Transport = "transport-udp",
            Codecs = "ulaw,alaw",
            RegistrationUri = "sip:sip.carrier.example",
            Context = "from-trunk",
        }, AuthenticatedPlatformApiFactory.TestTenantId);

        var response = await _admin.PutAsJsonAsync($"/api/v1/admin/trunks/{id}",
            new { registrationUri = "sip:sip2.carrier.example" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<long> CreateRegistrationTrunkAsync(string name)
    {
        var create = await _admin.PostAsJsonAsync("/api/v1/admin/trunks",
            RegistrationBody(name, "sip:sip.carrier.example", "sip:acct100@sip.carrier.example"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }
}
