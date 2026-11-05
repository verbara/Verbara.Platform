using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// Level 1 stays off until the host enables it (spec <c>webchat-visitor-identity</c>, "Level 1 MUST stay off until the
/// host enables it"): a console that does not render the unverified mark must not be shown linked history. Each test
/// builds its host with the setting absent, <c>false</c> or <c>true</c>.
/// </summary>
public sealed class WebChatVisitorLevel1SwitchTests
{
    private const string SettingKey = "WebChat:VisitorIdentity:Level1Enabled";

    private static readonly JsonObject SessionFixture = LoadFixture("webchat-session-create.v1.json");
    private static readonly JsonObject TokenFixture = LoadFixture("webchat-identity-token.v1.json");
    private static readonly JsonObject LevelFixture = LoadFixture("contact-identity-level.v1.json");

    private static readonly JsonObject FixtureRequest = SessionFixture["request"]!["body"]!.AsObject();
    private static readonly string TenantValue = FixtureRequest["tenantId"]!.GetValue<string>();
    private static readonly TenantId Tenant = new(TenantValue);
    private static readonly string VisitorId = FixtureRequest["visitorId"]!.GetValue<string>();
    private static readonly string CurrentSecret = TokenFixture["testVector"]!["currentSecret"]!.GetValue<string>();
    private static readonly string VerifiedExternalKey =
        TokenFixture["testVector"]!["accepted"]!.AsArray()[0]!["contactAddress"]!.GetValue<string>();
    private static readonly string ExpiredToken =
        TokenFixture["testVector"]!["refused"]!.AsArray()
            .First(r => r!["identityTokenRefusal"]!.GetValue<string>() == "identity-token-expired")!["token"]!.GetValue<string>();
    private static readonly string VisitorKey =
        System.Text.RegularExpressions.Regex.Match(
            LevelFixture.ToJsonString(),
            System.Text.RegularExpressions.Regex.Escape(ChannelAddress.WebChatVisitorKeyPrefix) + "[A-Za-z0-9_-]{43}").Value;
    private static readonly DateTimeOffset FixtureNow =
        DateTimeOffset.FromUnixTimeSeconds(TokenFixture["testVector"]!["now"]!.GetValue<long>());

    private static JsonObject LoadFixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", name)))!.AsObject();

    private sealed class Factory(string? level1Enabled) : AuthenticatedPlatformApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (level1Enabled is not null)
                builder.UseSetting(SettingKey, level1Enabled);
        }

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

    private static Factory HostWith(string? level1Enabled) => new(level1Enabled) { ApiKeyTenantId = TenantValue };

    private sealed record Created(string Body)
    {
        private JsonElement Root => JsonDocument.Parse(Body).RootElement.Clone();
        public EntityId ContactId => EntityId.From(Root.GetProperty("contactId").GetString()!);
        public string ConversationId => Root.GetProperty("conversationId").GetString()!;
        public string IdentityLevel => Root.GetProperty("identityLevel").GetString()!;
        public string? Refusal => Root.TryGetProperty("identityTokenRefusal", out var refusal) ? refusal.GetString() : null;
    }

    private static async Task<Created> CreateAsync(Factory factory, JsonObject body)
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var response = await client.PostAsync(new Uri("/api/v1/webchat/sessions", UriKind.Relative), content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return new Created(text);
    }

    private static JsonObject VisitorBody(string? token = null)
    {
        var body = new JsonObject { ["tenantId"] = TenantValue, ["visitorId"] = VisitorId };
        if (token is not null)
            body["identityToken"] = token;
        return body;
    }

    private static async Task<string[]> AddressesOfTheTenantAsync(Factory factory)
    {
        var contacts = factory.Services.GetRequiredService<IContactStore>();
        var page = await contacts.SearchAsync(Tenant, null, new PagedQuery(1, 200), CancellationToken.None);
        return [.. page.Items.SelectMany(c => c.Addresses).Select(a => a.Address)];
    }

    private static async Task<string[]> ListedConversationsAsync(Factory factory, EntityId contactId)
    {
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync(
            new Uri($"/api/v1/contacts/{contactId.Value}/conversations", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return [.. JsonNode.Parse(text)!["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>())];
    }

    private static async Task AssertNothingLinkedAsync(Factory factory)
    {
        var first = await CreateAsync(factory, VisitorBody());
        var second = await CreateAsync(factory, VisitorBody());

        second.ContactId.Should().NotBe(first.ContactId, "nothing is linked while level 1 is off");
        first.IdentityLevel.Should().Be("Anonymous");
        second.IdentityLevel.Should().Be("Anonymous");
        (await AddressesOfTheTenantAsync(factory))
            .Should().NotContain(a => a.StartsWith(ChannelAddress.WebChatVisitorKeyPrefix, StringComparison.Ordinal));
        (await ListedConversationsAsync(factory, second.ContactId))
            .Should().Equal([second.ConversationId], "a console of any version finds only this session's conversation");
    }

    [Fact]
    public async Task CreateSession_ShouldLinkNothing_WhenTheHostDoesNotSetLevel1()
    {
        await using var factory = HostWith(null);

        await AssertNothingLinkedAsync(factory);
    }

    [Fact]
    public async Task CreateSession_ShouldLinkNothing_WhenLevel1IsSetToFalse()
    {
        await using var factory = HostWith("false");

        await AssertNothingLinkedAsync(factory);
    }

    [Fact]
    public async Task CreateSession_ShouldLinkTheVisitor_WhenLevel1IsSetToTrue()
    {
        await using var factory = HostWith("true");

        var first = await CreateAsync(factory, VisitorBody());
        var second = await CreateAsync(factory, VisitorBody());

        second.ContactId.Should().Be(first.ContactId);
        first.IdentityLevel.Should().Be("Unverified");
        second.IdentityLevel.Should().Be("Unverified");
        (await AddressesOfTheTenantAsync(factory)).Should().Contain(VisitorKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task CreateSession_ShouldResolveVerified_WhenLevel1IsOffAndTheTokenVerifies(string? level1Enabled)
    {
        await using var factory = HostWith(level1Enabled);

        var created = await CreateAsync(factory, FixtureRequest.DeepClone().AsObject());

        created.IdentityLevel.Should().Be("Verified");
        var contact = await factory.Services.GetRequiredService<IContactStore>()
            .GetByIdAsync(Tenant, created.ContactId, CancellationToken.None);
        contact!.Addresses.Select(a => a.Address).Should().Contain(VerifiedExternalKey);
        contact.Addresses.Select(a => a.Address).Should().NotContain(VisitorKey);
    }

    [Fact]
    public async Task CreateSession_ShouldStayAnonymousAndNameTheCode_WhenLevel1IsOffAndTheTokenIsRefused()
    {
        await using var factory = HostWith(null);

        var first = await CreateAsync(factory, VisitorBody(ExpiredToken));
        var second = await CreateAsync(factory, VisitorBody(ExpiredToken));

        first.Refusal.Should().Be("identity-token-expired");
        first.IdentityLevel.Should().Be("Anonymous");
        second.IdentityLevel.Should().Be("Anonymous");
        second.ContactId.Should().NotBe(first.ContactId, "the fallback to level 1 is off too");
    }

    [Fact]
    public async Task CreateSession_ShouldLeaveALinkedContactAlone_WhenLevel1WasTurnedOff()
    {
        await using var factory = HostWith("false");
        var contacts = factory.Services.GetRequiredService<IContactStore>();
        var linked = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = FixtureNow };
        linked.AddAddress(new ChannelAddress(ChannelType.WebChat, VisitorKey));
        await contacts.SaveAsync(linked, CancellationToken.None);

        var created = await CreateAsync(factory, VisitorBody());

        created.ContactId.Should().NotBe(linked.ContactId, "turning it off stops new linking at once");
        created.IdentityLevel.Should().Be("Anonymous");
        var kept = await contacts.GetByIdAsync(Tenant, linked.ContactId, CancellationToken.None);
        kept!.Addresses.Select(a => a.Address).Should().Equal([VisitorKey], "the contact keeps its key and gains no session");
        kept.IdentityLevel.Should().Be(ContactIdentityLevel.Unverified, "a contact already linked keeps reading Unverified");
    }
}
