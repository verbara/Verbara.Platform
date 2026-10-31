using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Audit;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// whatsapp-works-for-real D15, spec <c>channel-credential-config</c> — the <c>/admin/channels</c> surface
/// describes credentials as <c>{key, isSet}</c> and never returns a value; a <c>PUT</c> merges key by key
/// (omitted or blank keeps, value replaces, null removes); a WhatsApp key outside its canonical schema is
/// refused; the audit record names changed keys only. Every test seeds the tenant's WhatsApp row itself,
/// so the shared fixture's order does not matter.
/// <para>
/// agent-console-contract-debt (C7): <c>GET /channels/available</c> is what an agent can start on, on the host's real
/// registry. Those tests run in a new Customer tenant each, because a channel configuration cannot be deleted.
/// </para>
/// </summary>
public sealed class ChannelConfigEndpointsTests :
    IClassFixture<AuthenticatedPlatformApiFactory>,
    IClassFixture<ConversationOwnershipApiFactory>,
    IClassFixture<PlatformTenantAuthenticatedApiFactory>,
    IClassFixture<PartnerApiFactory>
{
    private const string Route = "/api/v1/admin/channels/whatsapp";
    private const string AvailableRoute = "/api/v1/channels/available";

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly ConversationOwnershipApiFactory _ownership;
    private readonly PlatformTenantAuthenticatedApiFactory _platform;
    private readonly PartnerApiFactory _partner;
    private readonly HttpClient _client;

    public ChannelConfigEndpointsTests(
        AuthenticatedPlatformApiFactory factory,
        ConversationOwnershipApiFactory ownership,
        PlatformTenantAuthenticatedApiFactory platform,
        PartnerApiFactory partner)
    {
        _factory = factory;
        _ownership = ownership;
        _platform = platform;
        _partner = partner;
        _client = factory.CreateAuthenticatedClient();
    }

    private static TenantId Tenant => new(AuthenticatedPlatformApiFactory.TestTenantId);

    [Fact]
    public async Task PutChannel_ShouldNotEchoSecrets_WhenSaved()
    {
        var secrets = NewSecrets();

        var response = await PutAsync(new JsonObject
        {
            [WhatsAppCredentialKeys.AccessToken] = secrets.AccessToken,
            [WhatsAppCredentialKeys.PhoneNumberId] = secrets.PhoneNumberId,
            [WhatsAppCredentialKeys.AppSecret] = secrets.AppSecret,
            [WhatsAppCredentialKeys.WebhookVerifyToken] = secrets.VerifyToken,
        });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain(secrets.AppSecret, "a saved secret is never echoed");
        body.Should().NotContain(secrets.AccessToken);
        body.Should().NotContain(secrets.VerifyToken);
        var isSet = IsSetByKey(body);
        isSet[WhatsAppCredentialKeys.AppSecret].Should().BeTrue();
        isSet[WhatsAppCredentialKeys.AccessToken].Should().BeTrue();
        isSet[WhatsAppCredentialKeys.WebhookVerifyToken].Should().BeTrue();
    }

    [Fact]
    public async Task PutChannel_ShouldKeepStoredSecret_WhenKeyOmitted()
    {
        var secrets = await SeedWhatsAppAsync();
        var newPhoneNumberId = $"pn-{Guid.NewGuid():N}";

        var response = await PutAsync(new JsonObject { [WhatsAppCredentialKeys.PhoneNumberId] = newPhoneNumberId });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var stored = await LoadAsync();
        stored.Credentials.Should().ContainKey(WhatsAppCredentialKeys.AppSecret, "an omitted key keeps its stored value");
        stored.Credentials[WhatsAppCredentialKeys.AppSecret].Should().Be(secrets.AppSecret);
        stored.Credentials[WhatsAppCredentialKeys.PhoneNumberId].Should().Be(newPhoneNumberId);
    }

    [Fact]
    public async Task PutChannel_ShouldKeepStoredSecret_WhenValueIsBlank()
    {
        var secrets = await SeedWhatsAppAsync();

        var response = await PutAsync(new JsonObject { [WhatsAppCredentialKeys.AppSecret] = "" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var stored = await LoadAsync();
        stored.Credentials.Should().ContainKey(WhatsAppCredentialKeys.AppSecret);
        stored.Credentials[WhatsAppCredentialKeys.AppSecret].Should().Be(secrets.AppSecret, "a blank value never erases a stored secret");
    }

    [Fact]
    public async Task PutChannel_ShouldRemoveKey_WhenValueIsNull()
    {
        await SeedWhatsAppAsync(wabaId: "waba-to-remove");

        var response = await PutAsync(new JsonObject { [WhatsAppCredentialKeys.WabaId] = null });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var stored = await LoadAsync();
        stored.Credentials.Should().NotContainKey(WhatsAppCredentialKeys.WabaId, "a null value removes the key");
        stored.Credentials.Should().ContainKey(WhatsAppCredentialKeys.AppSecret, "the other keys are kept");
    }

    [Fact]
    public async Task PutChannel_ShouldReturn400UnknownKey_WhenKeyIsNotInWhatsAppSchema()
    {
        var secrets = await SeedWhatsAppAsync();

        var response = await PutAsync(new JsonObject { ["AppSecrt"] = "typo-value" });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("unknown-credential-key");
        var stored = await LoadAsync();
        stored.Credentials.Should().NotContainKey("AppSecrt");
        stored.Credentials[WhatsAppCredentialKeys.AppSecret].Should().Be(secrets.AppSecret);
    }

    [Fact]
    public async Task PutChannel_ShouldAuditChangedKeyNamesOnly_WhenSecretReplaced()
    {
        var old = await SeedWhatsAppAsync();
        var newAppSecret = $"app-secret-new-{Guid.NewGuid():N}";

        var response = await PutAsync(new JsonObject { [WhatsAppCredentialKeys.AppSecret] = newAppSecret });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<IAuditStore>().SearchAsync(
            Tenant, new AuditQuery(Action: "channel.configured", Page: 1, PageSize: 500), CancellationToken.None);
        audit.Items.Should().Contain(
            e => e.Metadata != null
                 && e.Metadata.GetValueOrDefault("changed_keys") == WhatsAppCredentialKeys.AppSecret,
            "the record names the replaced key");
        // No reflection JSON here (AOT_JSON_STRICT runs): the record text of Changes plus every metadata pair.
        var recorded = string.Join('\n', audit.Items.Select(e =>
            $"{e.Changes?.Before} {e.Changes?.After} " +
            string.Join(';', (e.Metadata ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key}={kv.Value}"))));
        recorded.Should().NotContain(newAppSecret).And.NotContain(old.AppSecret);
    }

    [Fact]
    public async Task GetChannel_ShouldReportIsSet_WhenSecretStored()
    {
        var secrets = await SeedWhatsAppAsync(wabaId: null);

        var response = await _client.GetAsync(Route);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var isSet = IsSetByKey(body);
        isSet.Should().ContainKey(WhatsAppCredentialKeys.AppSecret).WhoseValue.Should().BeTrue();
        isSet.Should().ContainKey(WhatsAppCredentialKeys.WabaId).WhoseValue.Should().BeFalse(
            "every canonical key is listed, isSet false when absent");
        body.Should().NotContain(secrets.AppSecret);
    }

    [Fact]
    public async Task ListChannels_ShouldNotContainCredentialValues_WhenSecretsStored()
    {
        var secrets = await SeedWhatsAppAsync(wabaId: $"waba-{Guid.NewGuid():N}");

        var response = await _client.GetAsync("/api/v1/admin/channels");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        foreach (var value in secrets.AllValues)
            body.Should().NotContain(value, "the channel list carries no credential value");
    }

    [Fact]
    public async Task PutChannel_ShouldStoreDefaultQueueId_WhenWhatsAppRoutingKeySent()
    {
        await SeedWhatsAppAsync();
        var queueId = $"q-{Guid.NewGuid():N}";

        var response = await PutAsync(new JsonObject { [ChannelCredentialSchemas.DefaultQueueIdKey] = queueId });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        (await LoadAsync()).Credentials[ChannelCredentialSchemas.DefaultQueueIdKey].Should().Be(
            queueId, "inbound routing reads the WhatsApp default queue from the stored credentials");
        IsSetByKey(body)[ChannelCredentialSchemas.DefaultQueueIdKey].Should().BeTrue();
    }

    [Fact]
    public async Task GetChannel_ShouldListLegacyKey_WhenStoredOutsideSchema()
    {
        var legacyToken = await SeedLegacyWhatsAppAsync();

        var response = await _client.GetAsync(Route);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        IsSetByKey(body).Should().ContainKey("ApiToken").WhoseValue.Should().BeTrue(
            "a stored key outside the schema is shown so the administrator can remove it");
        body.Should().NotContain(legacyToken);
    }

    [Fact]
    public async Task PutChannel_ShouldRemoveLegacyKey_WhenValueIsNull()
    {
        await SeedLegacyWhatsAppAsync();

        var response = await PutAsync(new JsonObject { ["ApiToken"] = null, ["PhoneNumber"] = null });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var stored = await LoadAsync();
        stored.Credentials.Should().NotContainKey("ApiToken", "a null removes a legacy key outside the schema");
        stored.Credentials.Should().NotContainKey("PhoneNumber");
        stored.Credentials.Should().ContainKey(WhatsAppCredentialKeys.AppSecret);
    }

    [Theory]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Supervisor)]
    [InlineData(UserRole.Admin)]
    public async Task ListAvailableChannels_ShouldAnswerTheFixturesBody_WhenWhatsAppAndWebChatAreActive(UserRole role)
    {
        var secret = $"app-secret-{Guid.NewGuid():N}";
        var (client, tenant) = NewTenant(_ownership, role);
        await SaveChannelAsync(_ownership, tenant, ChannelType.WebChat);
        await SaveChannelAsync(_ownership, tenant, ChannelType.WhatsApp, credentials: new Dictionary<string, string>
        {
            [WhatsAppCredentialKeys.AppSecret] = secret,
        });

        var response = await client.GetAsync(AvailableRoute);

        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        JsonNode.DeepEquals(JsonNode.Parse(raw), AvailableFixture()["body"]).Should().BeTrue(
            $"the body is frozen, WhatsApp first, with no member beyond channel; it was {raw}");
        raw.Should().NotContain(secret, "the list carries no configuration");
    }

    [Fact]
    public async Task ListAvailableChannels_ShouldAnswerTheFixturesEmptyBody_WhenNothingIsActive()
    {
        var (client, _) = NewTenant(_ownership);

        var response = await client.GetAsync(AvailableRoute);

        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        JsonNode.DeepEquals(JsonNode.Parse(raw), AvailableFixture()["emptyBody"]).Should().BeTrue($"it was {raw}");
    }

    [Fact]
    public async Task ListAvailableChannels_ShouldReturn401_WhenThereIsNoCredential()
    {
        // The tenant is named, so this 401 is the policy's and not the operational gate's own missing-tenant answer.
        using var client = _ownership.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", ConversationOwnershipApiFactory.CustomerTenantId);

        var response = await client.GetAsync(AvailableRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListAvailableChannels_ShouldReturnTheGatesProblem_WhenTheTenantIsNotOperational()
    {
        foreach (var caller in new[] { _platform.CreateAuthenticatedClient(), _partner.CreatePartnerClient() })
        {
            var response = await caller.GetAsync(AvailableRoute);

            var raw = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, raw);
            JsonNode.Parse(raw)!["type"]!.GetValue<string>()
                .Should().Be("https://verbara.platform/errors/tenant-type-mismatch");
        }
    }

    [Fact]
    public async Task ListAvailableChannels_ShouldLeaveWhatsAppOut_WhenItIsRegisteredButNotConfigured()
    {
        var (client, tenant) = NewTenant(_ownership);
        await SaveChannelAsync(_ownership, tenant, ChannelType.WebChat);

        (await ReadAvailableAsync(client)).Should().Equal("WebChat");
    }

    [Fact]
    public async Task ListAvailableChannels_ShouldLeaveWhatsAppOut_WhenItsConfigurationIsInactive()
    {
        var (client, tenant) = NewTenant(_ownership);
        await SaveChannelAsync(_ownership, tenant, ChannelType.WhatsApp, isActive: false);
        await SaveChannelAsync(_ownership, tenant, ChannelType.WebChat);

        (await ReadAvailableAsync(client)).Should().Equal("WebChat");
    }

    [Theory]
    [InlineData(ChannelType.Sms)]
    [InlineData(ChannelType.Voice)]
    public async Task ListAvailableChannels_ShouldLeaveTheChannelOut_WhenItIsActiveButNotRegistered(ChannelType channel)
    {
        var (client, tenant) = NewTenant(_ownership);
        await SaveChannelAsync(_ownership, tenant, channel);

        (await ReadAvailableAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListAvailableChannels_ShouldListAChannel_WhenTheHostStartsRegisteringIt()
    {
        await using var host = new LaterConnectorApiFactory();
        var (client, tenant) = NewTenant(host);
        await SaveChannelAsync(host, tenant, ChannelType.WebChat);
        await SaveChannelAsync(host, tenant, ChannelType.Sms);
        await SaveChannelAsync(host, tenant, ChannelType.WhatsApp);

        (await ReadAvailableAsync(client)).Should().Equal("WhatsApp", "Sms", "WebChat");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static JsonObject AvailableFixture()
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", "available-channels.v1.json");
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    /// <summary>A new Customer tenant with no channel configuration, and a client of a user of it with no Agent profile.</summary>
    private static (HttpClient Client, TenantId Tenant) NewTenant(
        ConversationOwnershipApiFactory host, UserRole role = UserRole.Agent)
    {
        var tenant = $"channels-{Guid.NewGuid():N}";
        AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, tenant);
        var user = host.SaveUser($"reader-{Guid.NewGuid():N}", tenant, role, UserStatus.Active);
        return (host.ClientFor(user), new TenantId(tenant));
    }

    private static Task SaveChannelAsync(
        ConversationOwnershipApiFactory host,
        TenantId tenant,
        ChannelType channel,
        bool isActive = true,
        IReadOnlyDictionary<string, string>? credentials = null) =>
        host.Services.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(
            new TenantChannelConfig
            {
                TenantId = tenant,
                Channel = channel,
                IsActive = isActive,
                Credentials = credentials ?? new Dictionary<string, string>(),
            },
            CancellationToken.None);

    private static async Task<string[]> ReadAvailableAsync(HttpClient client)
    {
        var response = await client.GetAsync(AvailableRoute);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        return JsonNode.Parse(raw)!.AsArray().Select(item => item!["channel"]!.GetValue<string>()).ToArray();
    }

    /// <summary>A host with one more connector than the code registers today: what a later channel module adds to DI.</summary>
    private sealed class LaterConnectorApiFactory : ConversationOwnershipApiFactory
    {
        /// <inheritdoc />
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            base.ConfigureTestServices(services);

            var connector = Substitute.For<IChannelConnector>();
            connector.Channel.Returns(ChannelType.Sms);
            services.AddSingleton(connector);
        }
    }

    private sealed record Secrets(string AccessToken, string PhoneNumberId, string AppSecret, string VerifyToken, string? WabaId)
    {
        public IEnumerable<string> AllValues =>
            new[] { AccessToken, PhoneNumberId, AppSecret, VerifyToken, WabaId }.OfType<string>();
    }

    private static Secrets NewSecrets(string? wabaId = null)
    {
        var n = Guid.NewGuid().ToString("N");
        return new Secrets($"EAAtoken{n}", $"pn{n}", $"app-secret-{n}", $"verify-{n}", wabaId);
    }

    private async Task<Secrets> SeedWhatsAppAsync(string? wabaId = null)
    {
        var secrets = NewSecrets(wabaId);
        var credentials = new Dictionary<string, string>
        {
            [WhatsAppCredentialKeys.AccessToken] = secrets.AccessToken,
            [WhatsAppCredentialKeys.PhoneNumberId] = secrets.PhoneNumberId,
            [WhatsAppCredentialKeys.AppSecret] = secrets.AppSecret,
            [WhatsAppCredentialKeys.WebhookVerifyToken] = secrets.VerifyToken,
        };
        if (wabaId is not null)
            credentials[WhatsAppCredentialKeys.WabaId] = wabaId;

        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(
            new TenantChannelConfig
            {
                TenantId = Tenant,
                Channel = ChannelType.WhatsApp,
                IsActive = true,
                Credentials = credentials,
            },
            CancellationToken.None);
        return secrets;
    }

    /// <summary>A row as the console saved it before Web #393: the Meta token under ApiToken.</summary>
    private async Task<string> SeedLegacyWhatsAppAsync()
    {
        var secrets = NewSecrets();
        var legacyToken = $"EAAlegacy{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(
            new TenantChannelConfig
            {
                TenantId = Tenant,
                Channel = ChannelType.WhatsApp,
                IsActive = true,
                Credentials = new Dictionary<string, string>
                {
                    ["ApiToken"] = legacyToken,
                    ["PhoneNumber"] = "+15550001111",
                    [WhatsAppCredentialKeys.AppSecret] = secrets.AppSecret,
                },
            },
            CancellationToken.None);
        return legacyToken;
    }

    private async Task<TenantChannelConfig> LoadAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var config = await scope.ServiceProvider.GetRequiredService<ITenantChannelConfigStore>()
            .GetAsync(Tenant, ChannelType.WhatsApp, CancellationToken.None);
        config.Should().NotBeNull();
        return config!;
    }

    private async Task<HttpResponseMessage> PutAsync(JsonObject credentials)
    {
        var body = new JsonObject { ["isActive"] = true, ["credentials"] = credentials };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await _client.PutAsync(Route, content);
    }

    /// <summary>Reads the <c>credentials</c> array of <c>{key, isSet}</c> entries into a map.</summary>
    private static Dictionary<string, bool> IsSetByKey(string body)
    {
        var credentials = JsonNode.Parse(body)!["credentials"];
        credentials.Should().BeOfType<JsonArray>("credentials are described as a list of {key, isSet} entries");
        return credentials!.AsArray().ToDictionary(
            entry => entry!["key"]!.GetValue<string>(),
            entry => entry!["isSet"]!.GetValue<bool>());
    }
}
