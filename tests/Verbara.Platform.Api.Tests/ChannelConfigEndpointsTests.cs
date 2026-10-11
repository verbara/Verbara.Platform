using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Audit;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// whatsapp-works-for-real D15, spec <c>channel-credential-config</c> — the <c>/admin/channels</c> surface
/// describes credentials as <c>{key, isSet}</c> and never returns a value; a <c>PUT</c> merges key by key
/// (omitted or blank keeps, value replaces, null removes); a WhatsApp key outside its canonical schema is
/// refused; the audit record names changed keys only. Every test seeds the tenant's WhatsApp row itself,
/// so the shared fixture's order does not matter.
/// </summary>
public sealed class ChannelConfigEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private const string Route = "/api/v1/admin/channels/whatsapp";

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public ChannelConfigEndpointsTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
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

    // ── helpers ───────────────────────────────────────────────────────────────

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
