using System.Net;
using System.Net.Http.Headers;
using System.Buffers.Text;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// How <c>POST /webchat/sessions</c> resolves its contact by identity level (spec <c>webchat-visitor-identity</c>,
/// <c>Conversations/Fixtures/webchat-session-create.v1.json</c>), through the real host over the in-memory stores, at the
/// token fixture's <c>now</c>. Each test gets its own host, so row counts start at zero.
/// </summary>
public sealed class WebChatSessionIdentityLevelTests : IDisposable
{
    private const string SessionAddressKey = "_webchatSessionAddress";
    private const string SaturatedCode = "webchat-visitor-key-saturated";

    private static readonly JsonObject SessionFixture = LoadFixture("webchat-session-create.v1.json");
    private static readonly JsonObject TokenFixture = LoadFixture("webchat-identity-token.v1.json");
    private static readonly JsonObject LevelFixture = LoadFixture("contact-identity-level.v1.json");

    private static readonly JsonObject FixtureRequest = SessionFixture["request"]!["body"]!.AsObject();
    private static readonly string TenantValue = FixtureRequest["tenantId"]!.GetValue<string>();
    private static readonly TenantId Tenant = new(TenantValue);
    private static readonly string VisitorId = FixtureRequest["visitorId"]!.GetValue<string>();
    private static readonly string ValidToken = FixtureRequest["identityToken"]!.GetValue<string>();
    private static readonly string VisitorKey = FirstKeyOf(LevelFixture, ChannelAddress.WebChatVisitorKeyPrefix);
    private static readonly string CurrentSecret = TokenFixture["testVector"]!["currentSecret"]!.GetValue<string>();
    private static readonly DateTimeOffset FixtureNow =
        DateTimeOffset.FromUnixTimeSeconds(TokenFixture["testVector"]!["now"]!.GetValue<long>());

    private const string OtherVisitorId = "9d1b7a3e-52c4-4f8a-a6d0-3e7c1b2f4a59";
    private const string VerifiedExternalKey = "webchat-external:cust-48151623";

    private readonly Factory _factory = new() { CaptureLogs = true };

    public void Dispose() => _factory.Dispose();

    private static JsonObject LoadFixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", name)))!.AsObject();

    private static string FirstKeyOf(JsonObject fixture, string prefix)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            fixture.ToJsonString(), System.Text.RegularExpressions.Regex.Escape(prefix) + "[A-Za-z0-9_-]{43}");
        match.Success.Should().BeTrue();
        return match.Value;
    }

    private sealed class Factory : AuthenticatedPlatformApiFactory
    {
        public MutableClockForSessions Clock { get; } = new(FixtureNow);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("WebChat:VisitorIdentity:Level1Enabled", "true");
            builder.UseSetting(WebChatRateLimitPolicy.SessionsLimitKey, "1000");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureServices((_, services) =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(Clock);
                var registered = services.Last(d => d.ServiceType == typeof(IContactStore));
                services.Remove(registered);
                services.AddSingleton<HookedContactStore>(sp =>
                    new HookedContactStore((IContactStore)ActivatorUtilities.CreateInstance(sp, registered.ImplementationType!)));
                services.AddSingleton<IContactStore>(sp => sp.GetRequiredService<HookedContactStore>());
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
            SeedWebChatChannel(host.Services, TestTenantId);
            return host;
        }
    }

    internal sealed class MutableClockForSessions(DateTimeOffset start) : IClock
    {
        private long _ticks = start.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>Passes everything to the store, and runs a hook after a find-or-insert found a contact.</summary>
    internal sealed class HookedContactStore(IContactStore inner) : IContactStore
    {
        public Func<IContactStore, Contact, Task>? AfterFound { get; set; }

        public Task<Contact?> GetByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) => inner.GetByIdAsync(tenantId, contactId, ct);
        public Task<Contact?> FindByAddressAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct) => inner.FindByAddressAsync(tenantId, address, ct);
        public Task<IReadOnlyList<Contact>> FindAllByChannelAddressAsync(TenantId tenantId, ChannelType channel, string address, int limit, CancellationToken ct) => inner.FindAllByChannelAddressAsync(tenantId, channel, address, limit, ct);
        public Task<PagedResult<Contact>> SearchAsync(TenantId tenantId, string? searchTerm, PagedQuery query, CancellationToken ct) => inner.SearchAsync(tenantId, searchTerm, query, ct);
        public Task SaveAsync(Contact contact, CancellationToken ct) => inner.SaveAsync(contact, ct);
        public Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) => inner.DeleteAsync(tenantId, contactId, ct);

        public Task<Contact?> AddIdentityAsync(
            TenantId tenantId, EntityId contactId, IReadOnlyList<ChannelAddress> addresses, string? firstNameIfUnnamed,
            DateTimeOffset updatedAt, string updatedBy, CancellationToken ct) =>
            inner.AddIdentityAsync(tenantId, contactId, addresses, firstNameIfUnnamed, updatedAt, updatedBy, ct);

        public async Task<Contact> FindOrInsertByAddressAsync(TenantId tenantId, ChannelAddress key, Contact newContact, CancellationToken ct)
        {
            var held = await inner.FindOrInsertByAddressAsync(tenantId, key, newContact, ct);
            if (held.ContactId != newContact.ContactId && AfterFound is { } hook)
            {
                AfterFound = null;
                await hook(inner, held);
            }

            return held;
        }

        public Task<Contact?> AttachWebChatSessionAsync(
            TenantId tenantId, EntityId contactId, ChannelAddress sessionAddress, string? firstNameIfUnnamed,
            DateTimeOffset updatedAt, string updatedBy, CancellationToken ct) =>
            inner.AttachWebChatSessionAsync(tenantId, contactId, sessionAddress, firstNameIfUnnamed, updatedAt, updatedBy, ct);

        public Task<Contact?> SetCustomFieldIfAbsentAsync(
            TenantId tenantId, EntityId contactId, string key, string value, DateTimeOffset updatedAt, string updatedBy, CancellationToken ct) =>
            inner.SetCustomFieldIfAbsentAsync(tenantId, contactId, key, value, updatedAt, updatedBy, ct);
    }

    private IContactStore Contacts => _factory.Services.GetRequiredService<IContactStore>();
    private IConversationStore Conversations => _factory.Services.GetRequiredService<IConversationStore>();

    private sealed record Created(HttpStatusCode Status, string Body)
    {
        private JsonElement Root => JsonElement.Parse(Body);
        public string SessionId => Root.GetProperty("sessionId").GetString()!;
        public string VisitorAddress => Root.GetProperty("visitorAddress").GetString()!;
        public EntityId ContactId => EntityId.From(Root.GetProperty("contactId").GetString()!);
        public EntityId ConversationId => EntityId.From(Root.GetProperty("conversationId").GetString()!);
        public string IdentityLevel => Root.GetProperty("identityLevel").GetString()!;
        public string? Refusal => Root.TryGetProperty("identityTokenRefusal", out var refusal) ? refusal.GetString() : null;
        public bool HasRefusalMember => Root.TryGetProperty("identityTokenRefusal", out _);
        public string[] MemberNames => [.. Root.EnumerateObject().Select(p => p.Name)];
    }

    private async Task<Created> PostAsync(JsonObject body)
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var response = await client.PostAsync(new Uri("/api/v1/webchat/sessions", UriKind.Relative), content);
        return new Created(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonObject Body(
        string? visitorId = null, string? token = null, string? name = null, string? email = null, string? tenantId = null)
    {
        var body = new JsonObject { ["tenantId"] = tenantId ?? TenantValue };
        if (visitorId is not null)
            body["visitorId"] = visitorId;
        if (token is not null)
            body["identityToken"] = token;
        if (name is not null)
            body["visitorName"] = name;
        if (email is not null)
            body["visitorEmail"] = email;
        return body;
    }

    private async Task<Created> CreateAsync(
        string? visitorId = null, string? token = null, string? name = null, string? email = null, string? tenantId = null)
    {
        var created = await PostAsync(Body(visitorId, token, name, email, tenantId));
        created.Status.Should().Be(HttpStatusCode.OK, created.Body);
        return created;
    }

    private async Task<Contact> ContactAsync(EntityId contactId) =>
        (await Contacts.GetByIdAsync(Tenant, contactId, CancellationToken.None))!;

    private async Task<IReadOnlyList<Contact>> AllContactsAsync() =>
        (await Contacts.SearchAsync(Tenant, null, new PagedQuery(1, 200), CancellationToken.None)).Items;

    private static string[] Addresses(Contact contact) => [.. contact.Addresses.Select(a => a.Address)];

    private static string Signature(Contact c) =>
        $"{string.Join(',', Addresses(c))}|{c.FirstName}|{c.LastName}|{c.UpdatedAt:O}|{c.UpdatedBy}|{string.Join(',', c.CustomFields.Select(kv => kv.Key + "=" + kv.Value))}";

    private static string RefusedToken(string code) =>
        TokenFixture["testVector"]!["refused"]!.AsArray()
            .First(r => r!["identityTokenRefusal"]!.GetValue<string>() == code)!["token"]!.GetValue<string>();

    private static void AssertShapeOfCase(int fixtureCase, Created created)
    {
        var expected = SessionFixture["responses"]!.AsArray()[fixtureCase]!;
        created.Status.Should().Be((HttpStatusCode)expected["status"]!.GetValue<int>());
        var body = expected["body"]!.AsObject();
        created.MemberNames.Should().BeEquivalentTo(body.Select(kv => kv.Key), "the fixture names the members of this case");
        created.IdentityLevel.Should().Be(body["identityLevel"]!.GetValue<string>());
        created.Refusal.Should().Be(body["identityTokenRefusal"]?.GetValue<string>());
        created.VisitorAddress.Should().MatchRegex("^webchat-[0-9a-f]{32}$", "the session address is never an identity key");
    }

    // A token of the fixture's customer, signed with the fixture's secret, without the named claims.
    private static string TokenWithout(params string[] claims)
    {
        var payload = TokenFixture["testVector"]!["payload"]!.DeepClone().AsObject();
        foreach (var claim in claims)
            payload.Remove(claim);
        var unsigned = Base64Url.EncodeToString("""{"alg":"HS256","typ":"JWT"}"""u8) + "."
            + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(CurrentSecret), Encoding.UTF8.GetBytes(unsigned));
        return unsigned + "." + Base64Url.EncodeToString(signature);
    }

    private async Task<Contact> SeedOpenConversationsAsync(string visitorId, int openConversations)
    {
        var first = await CreateAsync(visitorId);
        var lifecycle = _factory.Services.GetRequiredService<IConversationLifecycleService>();
        for (var i = 1; i < openConversations; i++)
        {
            await lifecycle.CreateForWebChatSessionAsync(
                Tenant, first.ContactId, new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}"), CancellationToken.None);
        }

        return await ContactAsync(first.ContactId);
    }

    private async Task<Contact> SeedContactHoldingBothKeysAsync()
    {
        var contact = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = FixtureNow };
        contact.AddAddress(new ChannelAddress(ChannelType.WebChat, VerifiedExternalKey));
        contact.AddAddress(new ChannelAddress(ChannelType.WebChat, VisitorKey));
        await Contacts.SaveAsync(contact, CancellationToken.None);
        return contact;
    }

    private List<string> SaturationWarnings() =>
        [.. _factory.CapturedLogs.RecordsContaining(SaturatedCode).Where(r => r.Level == LogLevel.Warning).Select(r => r.Message)];

    // ─── Review round: a verified contact's name and e-mail come only from the token ─────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldStoreNoFormData_WhenTheVerifiedTokenOmitsNameAndEmail()
    {
        var created = await CreateAsync(token: TokenWithout("name", "email"), name: "Escrito en el formulario", email: "form@example.com");

        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Verified));
        var contact = await ContactAsync(created.ContactId);
        contact.FirstName.Should().BeNull("a verified contact takes its name only from the token");
        contact.CustomFields.Should().NotContainKey(Contact.WebChatEmailField);
    }

    [Fact]
    public async Task CreateSession_ShouldNotFillAVerifiedContactFromTheForm_WhenTheContactAlreadyExists()
    {
        var first = await CreateAsync(token: TokenWithout("name", "email"));

        var second = await CreateAsync(token: TokenWithout("name", "email"), name: "Escrito en el formulario", email: "form@example.com");

        second.ContactId.Should().Be(first.ContactId);
        var contact = await ContactAsync(second.ContactId);
        contact.FirstName.Should().BeNull();
        contact.CustomFields.Should().NotContainKey(Contact.WebChatEmailField);
    }

    [Fact]
    public async Task CreateSession_ShouldTakeOnlyTheClaimTheTokenCarries_WhenTheFormGivesBoth()
    {
        var created = await CreateAsync(token: TokenWithout("email"), name: "Escrito en el formulario", email: "form@example.com");

        var contact = await ContactAsync(created.ContactId);
        contact.FirstName.Should().Be("Ana Pérez");
        contact.CustomFields.Should().NotContainKey(Contact.WebChatEmailField);
    }

    // Preservation control, green before the fix: a refused token leaves the session at the level the rest proves, and
    // that contact is not Verified, so the form still fills it.
    [Fact]
    public async Task CreateSession_ShouldKeepTheFormData_WhenTheTokenIsRefusedAndTheContactIsNotVerified()
    {
        var created = await CreateAsync(
            visitorId: VisitorId, token: RefusedToken("identity-token-expired"), name: "Escrito en el formulario", email: "form@example.com");

        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Unverified));
        var contact = await ContactAsync(created.ContactId);
        contact.FirstName.Should().Be("Escrito en el formulario");
        contact.CustomFields.Should().Contain(Contact.WebChatEmailField, "form@example.com");
    }

    // ─── responses[] of the fixture ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldAnswerFixtureCaseVerified_WhenTokenVerifies()
    {
        var created = await PostAsync(FixtureRequest.DeepClone().AsObject());

        AssertShapeOfCase(0, created);
        var contact = await ContactAsync(created.ContactId);
        Addresses(contact).Should().BeEquivalentTo([VerifiedExternalKey, created.VisitorAddress]);
        contact.FirstName.Should().Be("Ana Pérez", "the token's name, as the fixture's verified contact shows");
        contact.CustomFields.Should().Contain("webchatEmail", "ana.perez@example.com");
        contact.IdentityLevel.Should().Be(ContactIdentityLevel.Verified);
        (await Conversations.GetByIdAsync(Tenant, created.ConversationId, CancellationToken.None))!
            .Metadata.Should().Contain(SessionAddressKey, created.VisitorAddress);
    }

    [Fact]
    public async Task CreateSession_ShouldAnswerFixtureCaseUnverified_WhenVisitorReturns()
    {
        var first = await CreateAsync(VisitorId, name: "Ana");
        var second = await CreateAsync(VisitorId);

        AssertShapeOfCase(1, second);
        second.ContactId.Should().Be(first.ContactId, "the same browser finds its contact");
        second.ConversationId.Should().NotBe(first.ConversationId, "every session create opens a conversation");
        second.VisitorAddress.Should().NotBe(first.VisitorAddress);
        (await AllContactsAsync()).Should().ContainSingle("the tenant has no second contact");
        Addresses(await ContactAsync(first.ContactId)).Should().BeEquivalentTo([VisitorKey, first.VisitorAddress, second.VisitorAddress]);
    }

    [Fact]
    public async Task CreateSession_ShouldAnswerFixtureCaseRefused_WhenTokenExpiredAndVisitorIdIsUsable()
    {
        var first = await CreateAsync(VisitorId);

        var refused = await CreateAsync(VisitorId, RefusedToken("identity-token-expired"));

        AssertShapeOfCase(2, refused);
        refused.ContactId.Should().Be(first.ContactId);
    }

    [Fact]
    public async Task CreateSession_ShouldAnswerFixtureCaseAnonymous_WhenThereIsNoVisitorIdAndNoToken()
    {
        var created = await CreateAsync();

        AssertShapeOfCase(3, created);
        Addresses(await ContactAsync(created.ContactId)).Should().Equal([created.VisitorAddress]);
    }

    [Fact]
    public async Task CreateSession_ShouldAnswerFixtureCaseSaturated_WhenContactHoldsFiveOpenConversations()
    {
        var found = await SeedOpenConversationsAsync(VisitorId, 5);
        var before = Signature(found);

        var created = await CreateAsync(VisitorId);

        AssertShapeOfCase(4, created);
        created.ContactId.Should().NotBe(found.ContactId, "a saturated key gives this session a contact of its own");
        Signature(await ContactAsync(found.ContactId)).Should().Be(before, "nothing is added to the found contact");
        Addresses(await ContactAsync(created.ContactId)).Should().Equal([created.VisitorAddress]);
    }

    [Fact]
    public async Task CreateSession_ShouldAnswerFixtureCaseFailClosed_WhenTheKeyFindsAVerifiedContact()
    {
        var verified = await SeedContactHoldingBothKeysAsync();
        var before = Signature(verified);

        var created = await CreateAsync(VisitorId);

        AssertShapeOfCase(5, created);
        created.ContactId.Should().NotBe(verified.ContactId, "a browser key never reaches a verified contact");
        Signature(await ContactAsync(verified.ContactId)).Should().Be(before);
    }

    // ─── "A WebChat session MUST resolve exactly one contact by its identity level" ──────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldKeyTheContactOnTheHash_WhenVisitorIsNew()
    {
        var created = await CreateAsync(VisitorId);

        var contact = await ContactAsync(created.ContactId);
        Addresses(contact).Should().BeEquivalentTo([VisitorKey, created.VisitorAddress]);
        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Unverified));
        contact.IdentityLevel.Should().Be(ContactIdentityLevel.Unverified);
        var stored = string.Join('|', Addresses(contact).Concat(contact.CustomFields.Values).Append(contact.FirstName ?? ""));
        stored.Should().NotContain(VisitorId, "the raw visitor id is never stored");
    }

    [Fact]
    public async Task CreateSession_ShouldIgnoreTheVisitorId_WhenTokenVerifies()
    {
        var created = await CreateAsync(VisitorId, ValidToken);

        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Verified));
        (await AllContactsAsync()).SelectMany(Addresses).Should().NotContain(a => a.StartsWith(ChannelAddress.WebChatVisitorKeyPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateSession_ShouldReturnTheSameContact_WhenTokenVerifiesTwice()
    {
        var first = await CreateAsync(token: ValidToken);
        var second = await CreateAsync(OtherVisitorId, ValidToken);

        second.ContactId.Should().Be(first.ContactId);
        second.ConversationId.Should().NotBe(first.ConversationId);
        Addresses(await ContactAsync(first.ContactId)).Should().BeEquivalentTo([VerifiedExternalKey, first.VisitorAddress, second.VisitorAddress]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    [InlineData("3f2b8c1e-9a4d-1e6f-8b7a-1c2d3e4f5a6b")]
    public async Task CreateSession_ShouldKeepAContactOfThisSession_WhenVisitorIdIsNotARandomGuid(string visitorId)
    {
        var first = await CreateAsync(visitorId);
        var second = await CreateAsync(visitorId);

        first.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Anonymous));
        second.ContactId.Should().NotBe(first.ContactId, "nothing links the two sessions");
        (await AllContactsAsync()).SelectMany(Addresses).Should().NotContain(a => a.StartsWith(ChannelAddress.WebChatVisitorKeyPrefix, StringComparison.Ordinal));
    }

    // Spec "A returning visitor with an open conversation gets a new one" (design D16).
    [Fact]
    public async Task CreateSession_ShouldOpenANewConversationAndLeaveTheOldOne_WhenVisitorReturnsWithOneOpen()
    {
        var first = await CreateAsync(VisitorId);
        var k1 = await Conversations.GetByIdAsync(Tenant, first.ConversationId, CancellationToken.None);
        var stateBefore = k1!.State;

        var second = await CreateAsync(VisitorId);

        second.ConversationId.Should().NotBe(first.ConversationId);
        var kept = await Conversations.GetByIdAsync(Tenant, first.ConversationId, CancellationToken.None);
        kept!.State.Should().Be(stateBefore);
        kept.Metadata.Should().Contain(SessionAddressKey, first.VisitorAddress);
        (await Conversations.GetByIdAsync(Tenant, second.ConversationId, CancellationToken.None))!
            .ContactId.Should().Be(first.ContactId);
    }

    // ─── "Form data MUST NOT find or link a WebChat contact" ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldKeepTwoContacts_WhenTwoBrowsersGiveTheSameEmail()
    {
        var first = await CreateAsync(VisitorId, email: "ana@example.com");
        var second = await CreateAsync(OtherVisitorId, email: "ana@example.com");

        second.ContactId.Should().NotBe(first.ContactId, "an e-mail never links contacts");
        (await AllContactsAsync()).SelectMany(c => c.Addresses).Should().NotContain(a => a.Channel == ChannelType.Email);
    }

    [Fact]
    public async Task CreateSession_ShouldKeepTwoContacts_WhenTwoSessionsWithoutAVisitorIdGiveTheSameEmail()
    {
        var first = await CreateAsync(name: "Ana", email: "ana@example.com");
        var second = await CreateAsync(name: "Ana", email: "ana@example.com");

        second.ContactId.Should().NotBe(first.ContactId);
        var contact = await ContactAsync(first.ContactId);
        contact.FirstName.Should().Be("Ana");
        contact.CustomFields.Should().Contain("webchatEmail", "ana@example.com");
    }

    [Fact]
    public async Task CreateSession_ShouldFillTheNameOnlyWhenTheContactHasNone_WhenVisitorReturns()
    {
        var first = await CreateAsync(VisitorId);
        (await ContactAsync(first.ContactId)).FirstName.Should().BeNull();

        await CreateAsync(VisitorId, name: "Ana");
        (await ContactAsync(first.ContactId)).FirstName.Should().Be("Ana");

        await CreateAsync(VisitorId, name: "Otra");
        (await ContactAsync(first.ContactId)).FirstName.Should().Be("Ana", "a contact with a name keeps it");
    }

    // ─── "A refused identity token MUST NOT refuse the session" ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldStayAnonymousAndNameTheCode_WhenTokenIsRefusedAndThereIsNoVisitorId()
    {
        var created = await CreateAsync(token: RefusedToken("identity-token-signature-invalid"));

        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Anonymous));
        created.Refusal.Should().Be("identity-token-signature-invalid");
    }

    [Fact]
    public async Task CreateSession_ShouldNameNotConfigured_WhenTheTenantHasNoSecret()
    {
        var created = await CreateAsync(token: ValidToken, tenantId: AuthenticatedPlatformApiFactory.TestTenantId);

        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Anonymous));
        created.Refusal.Should().Be("identity-token-not-configured");
    }

    [Fact]
    public async Task CreateSession_ShouldOmitTheRefusalMember_WhenNoTokenWasSent()
    {
        (await CreateAsync(VisitorId)).HasRefusalMember.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateSession_ShouldTreatTheTokenAsAbsent_WhenItIsEmptyOrWhitespace(string token)
    {
        var withVisitor = await CreateAsync(VisitorId, token);
        var without = await CreateAsync(token: token);

        withVisitor.HasRefusalMember.Should().BeFalse();
        withVisitor.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Unverified));
        without.HasRefusalMember.Should().BeFalse();
        without.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Anonymous));
    }

    [Fact]
    public async Task CreateSession_ShouldLogNoSegmentOfAToken_WhenTokensAreAcceptedOrRefused()
    {
        var refused = RefusedToken("identity-token-signature-invalid");
        await CreateAsync(VisitorId, ValidToken);
        await CreateAsync(VisitorId, refused);
        await CreateAsync(VisitorId, RefusedToken("identity-token-expired"));

        _factory.CapturedLogs.RecordsContaining("identity-token-signature-invalid").Should().NotBeEmpty("a refusal is logged, with its code");
        foreach (var token in new[] { ValidToken, refused, RefusedToken("identity-token-expired") })
        {
            foreach (var segment in token.Split('.'))
                _factory.CapturedLogs.RecordsContaining(segment).Should().BeEmpty("no segment of a token reaches a log");
        }

        _factory.CapturedLogs.RecordsContaining(VisitorId).Should().BeEmpty();
        _factory.CapturedLogs.RecordsContaining(VisitorKey).Should().BeEmpty();
    }

    // ─── "A level-1 session MUST NOT resolve to a verified contact" ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldNotBecomeVerified_WhenTheKeyFindsAVerifiedContact()
    {
        var verified = await SeedContactHoldingBothKeysAsync();

        var created = await CreateAsync(VisitorId, name: "Ana", email: "ana@example.com");

        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Anonymous));
        var found = await ContactAsync(verified.ContactId);
        Addresses(found).Should().NotContain(created.VisitorAddress);
        found.FirstName.Should().BeNull();
        found.CustomFields.Should().BeEmpty();
        Addresses(await ContactAsync(created.ContactId)).Should().Equal([created.VisitorAddress]);
    }

    // ─── The unchanged refusals (controls: green on the code before this change as well) ─────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldAnswerTheFixture404_WhenTheTenantIsUnknown()
    {
        var body = FixtureRequest.DeepClone().AsObject();
        body["tenantId"] = "no-such-tenant";

        var created = await PostAsync(body);

        var refusal = SessionFixture["unchangedRefusals"]!.AsArray()[0]!;
        created.Status.Should().Be((HttpStatusCode)refusal["status"]!.GetValue<int>());
        JsonNode.DeepEquals(JsonNode.Parse(created.Body), refusal["body"]).Should().BeTrue(created.Body);
    }

    // ─── "The session request MUST bound and ignore the fields it does not use for identity" ─────────────────────────

    [Fact]
    public async Task CreateSession_ShouldAcceptAndNotStorePageContext_WhenRequestCarriesIt()
    {
        var body = FixtureRequest.DeepClone().AsObject();
        body.Remove("identityToken");

        var created = await PostAsync(body);

        created.Status.Should().Be(HttpStatusCode.OK, created.Body);
        var contact = await ContactAsync(created.ContactId);
        var conversation = await Conversations.GetByIdAsync(Tenant, created.ConversationId, CancellationToken.None);
        var stored = string.Join('|', contact.CustomFields.Keys.Concat(contact.CustomFields.Values)
            .Concat(conversation!.Metadata.Keys).Concat(conversation.Metadata.Values));
        stored.Should().NotContain("shop.example.com").And.NotContain("Order 123").And.NotContain("pageContext");
    }

    [Fact]
    public async Task CreateSession_ShouldCutTheNameAndDropTheEmail_WhenTheyAreOverTheirBounds()
    {
        var created = await CreateAsync(name: new string('n', 250), email: new string('e', 391) + "@example.com");

        var contact = await ContactAsync(created.ContactId);
        contact.FirstName.Should().Be(new string('n', 200));
        contact.CustomFields.Should().NotContainKey("webchatEmail");
    }

    [Fact]
    public async Task CreateSession_ShouldKeepTheEmail_WhenItIsExactlyAtItsBound()
    {
        var email = new string('e', 308) + "@example.com";
        email.Length.Should().Be(320);

        var created = await CreateAsync(email: email);

        (await ContactAsync(created.ContactId)).CustomFields.Should().Contain("webchatEmail", email);
    }

    [Fact]
    public async Task CreateSession_ShouldNotSplitACharacter_WhenTheCutFallsInsideOne()
    {
        var created = await CreateAsync(name: new string('n', 199) + "\U0001F600" + "tail");

        (await ContactAsync(created.ContactId)).FirstName.Should().Be(new string('n', 199));
    }

    // PostgreSQL's text and jsonb refuse U+0000, so form data carrying it must not reach the store.
    [Fact]
    public async Task CreateSession_ShouldDropTheNulCharacter_WhenFormDataCarriesIt()
    {
        var created = await CreateAsync(name: "A\u0000na", email: "ana\u0000@example.com");

        var contact = await ContactAsync(created.ContactId);
        contact.FirstName.Should().Be("Ana");
        contact.CustomFields.Should().Contain("webchatEmail", "ana@example.com");
    }

    // ─── "A saturated level-1 key MUST fall back to a contact of its own" ────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldStayOnTheContact_WhenItHoldsFourOpenConversations()
    {
        var found = await SeedOpenConversationsAsync(VisitorId, 4);

        var created = await CreateAsync(VisitorId);

        created.ContactId.Should().Be(found.ContactId, "the guard falls back at 5");
        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Unverified));
    }

    // The deterministic half of "Concurrent creates at the boundary can overshoot": what an overshoot leaves behind.
    [Fact]
    public async Task CreateSession_ShouldFallBack_WhenAnOvershootLeftSixOpenConversations()
    {
        var found = await SeedOpenConversationsAsync(VisitorId, 6);

        var created = await CreateAsync(VisitorId);

        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Anonymous));
        created.ContactId.Should().NotBe(found.ContactId);
    }

    [Fact]
    public async Task CreateSession_ShouldNotApplyTheGuard_WhenTokenVerifies()
    {
        var first = await CreateAsync(token: ValidToken);
        var lifecycle = _factory.Services.GetRequiredService<IConversationLifecycleService>();
        for (var i = 0; i < 5; i++)
        {
            await lifecycle.CreateForWebChatSessionAsync(
                Tenant, first.ContactId, new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}"), CancellationToken.None);
        }

        var created = await CreateAsync(token: ValidToken);

        created.ContactId.Should().Be(first.ContactId, "a level-2 key is proved by the tenant's signature");
        created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Verified));
    }

    [Fact]
    public async Task CreateSession_ShouldWarnOncePerTenantPerHourWithoutTheKey_WhenKeysAreSaturated()
    {
        await SeedOpenConversationsAsync(VisitorId, 5);

        await CreateAsync(VisitorId);
        await CreateAsync(VisitorId);
        SaturationWarnings().Should().ContainSingle("one warning per tenant per hour");
        var warning = SaturationWarnings()[0];
        warning.Should().Contain(TenantValue).And.Contain(SaturatedCode);
        warning.Should().NotContain(VisitorKey).And.NotContain(VisitorId);

        _factory.Clock.Advance(TimeSpan.FromMinutes(59));
        await CreateAsync(VisitorId);
        SaturationWarnings().Should().ContainSingle("the hour has not passed");

        _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await CreateAsync(VisitorId);
        SaturationWarnings().Should().HaveCount(2, "a new hour warns again");
    }

    // Spec "A constant key used by one visitor after another is not bounded": the accepted residual of design D17.
    [Fact]
    public async Task CreateSession_ShouldResolveOneContact_WhenAConstantKeyIsUsedOneVisitorAfterAnother()
    {
        var contacts = new HashSet<EntityId>();
        for (var i = 0; i < 7; i++)
        {
            var created = await CreateAsync(VisitorId);
            created.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Unverified));
            contacts.Add(created.ContactId);
            var conversation = await Conversations.GetByIdAsync(Tenant, created.ConversationId, CancellationToken.None);
            conversation!.State = ConversationState.Closed;
            conversation.ClosedAt = FixtureNow;
            await Conversations.SaveAsync(conversation, CancellationToken.None);
        }

        contacts.Should().ContainSingle();
    }

    // ─── "The first resolution of an identity key MUST be serialized" ──────────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldResolveOneContact_WhenTwoTabsOpenAtOnce()
    {
        var results = await Task.WhenAll(CreateAsync(VisitorId), CreateAsync(VisitorId));

        var holders = (await AllContactsAsync()).Where(c => Addresses(c).Contains(VisitorKey)).ToList();
        holders.Should().ContainSingle("the two tabs resolve one contact");
        results[0].ContactId.Should().Be(results[1].ContactId, "the two tabs resolve one contact");
        results.Should().OnlyContain(r => r.IdentityLevel == nameof(ContactIdentityLevel.Unverified));
        Addresses(holders[0]).Should().Contain([results[0].VisitorAddress, results[1].VisitorAddress]);
    }

    // ─── "A contact MUST keep a bounded number of session addresses" ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldDropTheOldestSessionAddress_WhenTheContactHoldsFifty()
    {
        var contact = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = FixtureNow };
        contact.AddAddress(new ChannelAddress(ChannelType.WebChat, VisitorKey));
        var seeded = Enumerable.Range(1, 50).Select(i => $"webchat-{i:x32}").ToList();
        foreach (var address in seeded)
            contact.AddAddress(new ChannelAddress(ChannelType.WebChat, address));
        await Contacts.SaveAsync(contact, CancellationToken.None);

        var created = await CreateAsync(VisitorId);

        created.ContactId.Should().Be(contact.ContactId);
        var held = Addresses(await ContactAsync(contact.ContactId));
        held.Should().Contain(VisitorKey, "keys are never pruned");
        held.Should().NotContain(seeded[0], "the oldest is dropped");
        held.Should().Contain(created.VisitorAddress);
        held.Count(a => a.StartsWith("webchat-", StringComparison.Ordinal) && !a.StartsWith(ChannelAddress.WebChatVisitorKeyPrefix, StringComparison.Ordinal))
            .Should().Be(50);
    }

    // Spec "A live session keeps its contact after its address was pruned".
    [Fact]
    public async Task VisitorMessage_ShouldKeepTheContactAndReachTheSession_WhenTheSessionAddressWasPruned()
    {
        var first = await CreateAsync(token: ValidToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var socket = await _factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri($"ws://localhost/ws/webchat/{first.SessionId}"), timeout.Token);
        (await ReceiveTextAsync(socket, TimeSpan.FromSeconds(5))).Should().Contain("connected");
        for (var i = 0; i < 50; i++)
            await CreateAsync(token: ValidToken);
        Addresses(await ContactAsync(first.ContactId)).Should().NotContain(first.VisitorAddress, "50 later sessions pruned it");
        var contactsBefore = (await AllContactsAsync()).Count;

        using var client = _factory.CreateClient();
        using var content = new StringContent("""{"text":"sigo aqui"}""", Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var send = await client.PostAsync(new Uri($"/api/v1/webchat/sessions/{first.SessionId}/messages", UriKind.Relative), content);
        send.StatusCode.Should().Be(HttpStatusCode.OK);

        (await AllContactsAsync()).Should().HaveCount(contactsBefore, "the message is attributed through the session binding");
        var messages = await _factory.Services.GetRequiredService<IMessageStore>()
            .GetConversationMessagesAsync(Tenant, first.ConversationId, 100, 0, CancellationToken.None);
        messages.Should().ContainSingle(m => m.Direction == MessageDirection.Inbound, "the message is in the session's conversation");
        var session = await _factory.Services.GetRequiredService<WebChatSessionManager>().GetSessionAsync(first.SessionId);
        session!.ContactId.Should().Be(first.ContactId);
        session.ConversationId.Should().Be(first.ConversationId);

        var agentId = EntityId.From($"agent-{Guid.NewGuid():N}");
        var conversation = await Conversations.GetByIdAsync(Tenant, first.ConversationId, CancellationToken.None);
        conversation!.State = ConversationState.Active;
        conversation.Owner = ConversationOwner.ForAgent(agentId);
        await Conversations.SaveAsync(conversation, CancellationToken.None);
        var reply = await _factory.Services.GetRequiredService<IConversationService>().SendMessageAsync(
            first.ConversationId, Tenant, new MessageEnvelope([new TextBlock("respuesta a la primera")]), agentId,
            ConversationOwnerKind.Agent, CancellationToken.None);

        reply.DeliveryStatus.Should().NotBe(MessageDeliveryStatus.Failed);
        (await ReceiveTextAsync(socket, TimeSpan.FromSeconds(3))).Should().Contain("respuesta a la primera");
    }

    // ─── The three webchatEmail scenarios ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_ShouldKeepAnAgentsEdit_WhenItLandsBetweenTheReadAndTheEmailWrite()
    {
        var first = await CreateAsync(VisitorId);
        _factory.Services.GetRequiredService<HookedContactStore>().AfterFound = async (store, found) =>
        {
            var edited = new Contact { ContactId = found.ContactId, TenantId = found.TenantId, CreatedAt = found.CreatedAt, LastName = "Editada" };
            foreach (var address in found.Addresses)
                edited.AddAddress(address);
            await store.SaveAsync(edited, CancellationToken.None);
        };

        await CreateAsync(VisitorId, email: "ana@example.com");

        var contact = await ContactAsync(first.ContactId);
        contact.LastName.Should().Be("Editada", "the agent's edit survives");
        contact.CustomFields.Should().Contain("webchatEmail", "ana@example.com");
    }

    [Fact]
    public async Task CreateSession_ShouldKeepTheEmail_WhenTheContactAlreadyHoldsOne()
    {
        var first = await CreateAsync(VisitorId, email: "ana@example.com");

        await CreateAsync(VisitorId, email: "other@example.com");

        (await ContactAsync(first.ContactId)).CustomFields.Should().Contain("webchatEmail", "ana@example.com");
    }

    [Fact]
    public async Task ContactSearch_ShouldFindTheContact_WhenSearchedByItsWebChatEmail()
    {
        var created = await CreateAsync(VisitorId, email: "ana@example.com", tenantId: AuthenticatedPlatformApiFactory.TestTenantId);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);

        using var response = await client.GetAsync(new Uri("/api/v1/contacts?search=ana@example.com", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(created.ContactId.Value);
    }

    // A contact erased between the find and the attach stays erased; the session gets a contact holding the key.
    [Fact]
    public async Task CreateSession_ShouldNotBringBackTheContact_WhenItIsErasedBetweenTheFindAndTheAttach()
    {
        var first = await CreateAsync(VisitorId, name: "Ana");
        _factory.Services.GetRequiredService<HookedContactStore>().AfterFound = (store, found) =>
            store.DeleteAsync(found.TenantId, found.ContactId, CancellationToken.None);

        var second = await CreateAsync(VisitorId);

        (await Contacts.GetByIdAsync(Tenant, first.ContactId, CancellationToken.None)).Should().BeNull();
        second.ContactId.Should().NotBe(first.ContactId);
        second.IdentityLevel.Should().Be(nameof(ContactIdentityLevel.Unverified));
        var contact = await ContactAsync(second.ContactId);
        Addresses(contact).Should().BeEquivalentTo([VisitorKey, second.VisitorAddress]);
        contact.FirstName.Should().BeNull("nothing of the erased contact comes back");
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        var buffer = new byte[8192];
        try
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buffer, 0, result.Count) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
