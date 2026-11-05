using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// Contact writes and the reserved WebChat identity keys (design D12, spec <c>webchat-visitor-identity</c>,
/// <c>contactWriteRefusals</c> of <c>Conversations/Fixtures/contact-identity-level.v1.json</c>), through the real host over
/// the in-memory stores. The API key belongs to the fixtures' tenant, so a contact written through the routes and a
/// session verified by the fixture's token are in one tenant. Each test gets its own host, so row counts start at zero.
/// </summary>
public sealed class ContactReservedAddressTests : IDisposable
{
    private static readonly JsonObject SessionFixture = LoadFixture("webchat-session-create.v1.json");
    private static readonly JsonObject TokenFixture = LoadFixture("webchat-identity-token.v1.json");
    private static readonly JsonObject LevelFixture = LoadFixture("contact-identity-level.v1.json");

    private static readonly JsonObject FixtureRequest = SessionFixture["request"]!["body"]!.AsObject();
    private static readonly string TenantValue = FixtureRequest["tenantId"]!.GetValue<string>();
    private static readonly TenantId Tenant = new(TenantValue);
    private static readonly string CurrentSecret = TokenFixture["testVector"]!["currentSecret"]!.GetValue<string>();
    private static readonly DateTimeOffset FixtureNow =
        DateTimeOffset.FromUnixTimeSeconds(TokenFixture["testVector"]!["now"]!.GetValue<long>());
    private static readonly string VerifiedExternalKey =
        TokenFixture["testVector"]!["accepted"]!.AsArray()[0]!["contactAddress"]!.GetValue<string>();

    private readonly Factory _factory = new() { ApiKeyTenantId = TenantValue };

    private IContactStore Contacts => _factory.Services.GetRequiredService<IContactStore>();

    public void Dispose() => _factory.Dispose();

    private static JsonObject LoadFixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", name)))!.AsObject();

    private sealed class Factory : AuthenticatedPlatformApiFactory
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureServices((_, services) =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new WebChatSessionIdentityLevelTests.MutableClockForSessions(FixtureNow));
            });
            var host = base.CreateHost(builder);
            SeedTestCustomerTenant(host.Services, TenantValue);
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(
                new TenantChannelConfig
                {
                    TenantId = Tenant,
                    Channel = ChannelType.WebChat,
                    Credentials = new Dictionary<string, string> { [ChannelCredentialSchemas.IdentityTokenSecretKey] = CurrentSecret },
                },
                CancellationToken.None).GetAwaiter().GetResult();
            return host;
        }
    }

    private async Task<Contact> ContactAsync(EntityId contactId) =>
        (await Contacts.GetByIdAsync(Tenant, contactId, CancellationToken.None))!;

    private async Task<IReadOnlyList<Contact>> AllContactsAsync() =>
        (await Contacts.SearchAsync(Tenant, null, new PagedQuery(1, 200), CancellationToken.None)).Items;

    private static string[] Addresses(Contact contact) => [.. contact.Addresses.Select(a => a.Address)];

    private static string Signature(Contact c) =>
        $"{string.Join(',', Addresses(c))}|{c.FirstName}|{c.LastName}|{c.UpdatedAt:O}|{c.UpdatedBy}|{string.Join(',', c.CustomFields.Select(kv => kv.Key + "=" + kv.Value))}";

    private const string SessionAddress = "webchat-6a7b8c9d0e1f42a3b4c5d6e7f8a9b0c1";
    private const string OtherPrefixedKey = "webchat-visitor:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private async Task<Contact> SeedContactAsync(string contactId)
    {
        var contact = new Contact { ContactId = EntityId.From(contactId), TenantId = Tenant, FirstName = "Antes", CreatedAt = FixtureNow };
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "5215512345678"));
        await Contacts.SaveAsync(contact, CancellationToken.None);
        return contact;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, JsonNode body)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantValue);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonObject> GetContactJsonAsync(string contactId)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantValue);
        return JsonNode.Parse(await client.GetStringAsync(new Uri($"/api/v1/contacts/{contactId}", UriKind.Relative)))!.AsObject();
    }

    private static JsonObject AddressBody(params (string Channel, string Address)[] addresses)
    {
        var list = new JsonArray();
        foreach (var (channel, address) in addresses)
            list.Add(new JsonObject { ["channel"] = channel, ["address"] = address });
        return new JsonObject { ["addresses"] = list };
    }

    private static (HttpMethod Method, string Path, string ContactId) RouteOf(string method)
    {
        const string contactId = "7a1c0e2b5d3f4a6b8c9d0e1f2a3b4c5d";
        return method == "POST"
            ? (HttpMethod.Post, "/api/v1/contacts", contactId)
            : (HttpMethod.Put, $"/api/v1/contacts/{contactId}", contactId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WriteContact_ShouldAnswerTheFixtureRefusal_WhenBodyNamesAReservedKey(int index)
    {
        var refusal = LevelFixture["contactWriteRefusals"]!.AsArray()[index]!;
        var seeded = await SeedContactAsync("7a1c0e2b5d3f4a6b8c9d0e1f2a3b4c5d");
        var before = Signature(seeded);

        var (status, body) = await SendAsync(
            new HttpMethod(refusal["request"]!["method"]!.GetValue<string>()),
            refusal["request"]!["path"]!.GetValue<string>(),
            refusal["request"]!["body"]!.DeepClone());

        status.Should().Be((HttpStatusCode)refusal["status"]!.GetValue<int>(), body);
        JsonNode.DeepEquals(JsonNode.Parse(body), refusal["body"]).Should().BeTrue($"the fixture fixes the answer, and it was {body}");
        (await AllContactsAsync()).Should().ContainSingle("a refused write creates no contact");
        Signature(await ContactAsync(seeded.ContactId)).Should().Be(before, "a refused write changes no contact");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task WriteContact_ShouldWriteNothing_WhenOneAddressOfTheBodyIsReserved(string method)
    {
        var route = RouteOf(method);
        var seeded = await SeedContactAsync(route.ContactId);
        var before = Signature(seeded);
        var body = AddressBody(("WhatsApp", "5215500000000"), ("WebChat", VerifiedExternalKey));
        body["firstName"] = "Cambiado";
        body["customFields"] = new JsonObject { ["campo"] = "valor" };

        var (status, answer) = await SendAsync(route.Method, route.Path, body);

        status.Should().Be(HttpStatusCode.BadRequest, answer);
        (await AllContactsAsync()).Should().ContainSingle("a refused write creates no contact");
        Signature(await ContactAsync(seeded.ContactId)).Should().Be(before, "the valid members of a refused body are not written either");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task WriteContact_ShouldNameEachOffendingAddressOnce_WhenBodyNamesSeveral(string method)
    {
        var route = RouteOf(method);
        await SeedContactAsync(route.ContactId);
        var body = AddressBody(
            ("WebChat", VerifiedExternalKey),
            ("WebChat", SessionAddress),
            ("WhatsApp", OtherPrefixedKey),
            ("WebChat", VerifiedExternalKey));
        body["addresses"]!.AsArray().Add(new JsonObject { ["channel"] = (int)ChannelType.WebChat, ["address"] = OtherPrefixedKey });

        var (status, answer) = await SendAsync(route.Method, route.Path, body);

        status.Should().Be(HttpStatusCode.BadRequest, answer);
        var refusal = JsonNode.Parse(answer)!;
        refusal["error"]!.GetValue<string>().Should().Be("reserved-contact-address");
        refusal["details"]!.AsArray().Select(d => d!.GetValue<string>()).Should().Equal(VerifiedExternalKey, OtherPrefixedKey);
    }

    // The check reads the body alone, so it answers before the contact is read.
    [Fact]
    public async Task PutContact_ShouldAnswerTheRefusal_WhenTheContactDoesNotExist()
    {
        var (status, answer) = await SendAsync(HttpMethod.Put, "/api/v1/contacts/nobody", AddressBody(("WebChat", VerifiedExternalKey)));

        status.Should().Be(HttpStatusCode.BadRequest, answer);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task WriteContact_ShouldAcceptTheAddress_WhenItIsASessionAddress(string method)
    {
        var route = RouteOf(method);
        await SeedContactAsync(route.ContactId);

        var (status, answer) = await SendAsync(route.Method, route.Path, AddressBody(("WebChat", SessionAddress)));

        status.Should().Be(method == "POST" ? HttpStatusCode.Created : HttpStatusCode.OK, answer);
        var written = JsonNode.Parse(answer)!;
        written["identityLevel"]!.GetValue<string>().Should().Be(nameof(ContactIdentityLevel.Anonymous));
        Addresses(await ContactAsync(EntityId.From(written["contactId"]!.GetValue<string>()))).Should().Contain(SessionAddress);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task WriteContact_ShouldAcceptTheAddress_WhenTheReservedPrefixIsOnAnotherChannel(string method)
    {
        var route = RouteOf(method);
        await SeedContactAsync(route.ContactId);

        var (status, answer) = await SendAsync(route.Method, route.Path, AddressBody(("WhatsApp", VerifiedExternalKey)));

        status.Should().Be(method == "POST" ? HttpStatusCode.Created : HttpStatusCode.OK, answer);
        JsonNode.Parse(answer)!.AsObject().ContainsKey("identityLevel").Should().BeFalse("a WhatsApp address has no WebChat level");
    }

    // Spec "A mixed-case prefix is an ordinary address". The contact and the session are in one tenant, so a session that
    // found it by that address would resolve to it.
    [Fact]
    public async Task PostContact_ShouldStayAnonymousAndUnfound_WhenThePrefixDiffersInCase()
    {
        const string mixedCase = "WebChat-External:cust-48151623";

        var (status, answer) = await SendAsync(HttpMethod.Post, "/api/v1/contacts", AddressBody(("WebChat", mixedCase)));

        status.Should().Be(HttpStatusCode.Created, answer);
        var written = JsonNode.Parse(answer)!;
        written["identityLevel"]!.GetValue<string>().Should().Be(nameof(ContactIdentityLevel.Anonymous));
        var contactId = EntityId.From(written["contactId"]!.GetValue<string>());
        var before = Signature(await ContactAsync(contactId));

        var (sessionStatus, sessionBody) = await SendAsync(HttpMethod.Post, "/api/v1/webchat/sessions", FixtureRequest.DeepClone());

        sessionStatus.Should().Be(HttpStatusCode.OK, sessionBody);
        var session = JsonNode.Parse(sessionBody)!;
        session["identityLevel"]!.GetValue<string>().Should().Be(nameof(ContactIdentityLevel.Verified));
        session["contactId"]!.GetValue<string>().Should().NotBe(contactId.Value, "the session's key is ordinal, so it does not find the mixed-case address");
        Signature(await ContactAsync(contactId)).Should().Be(before);
    }

    // Spec "identityLevel in a write body is ignored" (boundary H4).
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task WriteContact_ShouldIgnoreIdentityLevel_WhenBodyNamesIt(string method)
    {
        var route = RouteOf(method);
        await SeedContactAsync(route.ContactId);
        var body = AddressBody(("WhatsApp", "5215512345678"));
        body["identityLevel"] = nameof(ContactIdentityLevel.Verified);

        var (status, answer) = await SendAsync(route.Method, route.Path, body);

        status.Should().Be(method == "POST" ? HttpStatusCode.Created : HttpStatusCode.OK, answer);
        var written = JsonNode.Parse(answer)!.AsObject();
        written.ContainsKey("identityLevel").Should().BeFalse();
        (await GetContactJsonAsync(written["contactId"]!.GetValue<string>())).ContainsKey("identityLevel").Should().BeFalse();
    }

    // The same with a WebChat address: the level still comes from the address, not from the body.
    [Fact]
    public async Task PostContact_ShouldReadTheLevelOfItsAddress_WhenBodyNamesAnotherLevel()
    {
        var body = AddressBody(("WebChat", SessionAddress));
        body["identityLevel"] = nameof(ContactIdentityLevel.Verified);

        var (status, answer) = await SendAsync(HttpMethod.Post, "/api/v1/contacts", body);

        status.Should().Be(HttpStatusCode.Created, answer);
        JsonNode.Parse(answer)!["identityLevel"]!.GetValue<string>().Should().Be(nameof(ContactIdentityLevel.Anonymous));
    }
}
