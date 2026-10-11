using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// whatsapp-works-for-real (block A-in) — the WhatsApp webhook end to end through the real host: the
/// channel is registered (D1/D2), the delivery is verified with the tenant's own secret and bound to its
/// phone number id (D5), every event of a batch is persisted (D4), and the GET handshake checks the
/// tenant's verify token (D5).
/// </summary>
public sealed class WhatsAppWebhookTests : IClassFixture<PlatformApiFactory>
{
    private const string AppSecret = "wa-tenant-app-secret";
    private const string PhoneNumberId = "106540352242922";
    private const string VerifyToken = "wa-tenant-verify-token";

    private readonly PlatformApiFactory _factory;
    private readonly HttpClient _client;

    public WhatsAppWebhookTests(PlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static TenantId NewTenant() => new($"wa-{Guid.NewGuid():N}"[..20]);

    private async Task ConfigureTenantAsync(TenantId tenant)
    {
        await _factory.Services.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(new TenantChannelConfig
        {
            TenantId = tenant,
            Channel = ChannelType.WhatsApp,
            Credentials = new Dictionary<string, string>
            {
                ["AccessToken"] = "EAAtenant",
                ["PhoneNumberId"] = PhoneNumberId,
                ["AppSecret"] = AppSecret,
                ["WebhookVerifyToken"] = VerifyToken,
            },
        }, CancellationToken.None);

        await _factory.Services.GetRequiredService<IQueueStore>().SaveAsync(new Queue
        {
            QueueId = EntityId.New(),
            TenantId = tenant,
            Name = "wa-queue",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }

    private static string TextMessage(string id, string text) => $$"""
        { "from": "15550000002", "id": "{{id}}", "timestamp": "1700000000", "type": "text", "text": { "body": "{{text}}" } }
        """;

    private static byte[] Payload(string phoneNumberId, params string[] messages) =>
        Encoding.UTF8.GetBytes($$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABA",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "{{phoneNumberId}}" },
                    "contacts": [{ "profile": { "name": "Alice" }, "wa_id": "15550000002" }],
                    "messages": [{{string.Join(",", messages)}}]
                  }
                }]
              }]
            }
            """);

    private async Task<HttpResponseMessage> PostAsync(TenantId tenant, byte[] body, string secret)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        content.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(hash).ToLowerInvariant());
        return await _client.PostAsync($"/api/v1/webhooks/{tenant.Value}/whatsapp", content);
    }

    private Task<(int Contacts, int Conversations, int Messages)> CountRowsAsync(TenantId tenant) =>
        WebhookProbes.CountRowsAsync(_factory.Services, tenant);

    // ── POST ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostWebhook_ShouldCreateConversation_WhenMetaSignedBody()
    {
        var tenant = NewTenant();
        await ConfigureTenantAsync(tenant);

        var response = await PostAsync(tenant, Payload(PhoneNumberId, TextMessage("wamid.e1", "hola")), AppSecret);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        (await CountRowsAsync(tenant)).Should().Be((1, 1, 1));
    }

    [Fact]
    public async Task PostWebhook_ShouldCreateNothing_WhenSignatureIsWrong()
    {
        var tenant = NewTenant();
        await ConfigureTenantAsync(tenant);

        var response = await PostAsync(tenant, Payload(PhoneNumberId, TextMessage("wamid.bad", "forged")), "not-the-tenant-secret");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountRowsAsync(tenant)).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task PostWebhook_ShouldReturn404_WhenOtherTenantNotConfigured()
    {
        var configured = NewTenant();
        await ConfigureTenantAsync(configured);
        var other = NewTenant();

        // A body correctly signed for the configured tenant, posted to another tenant's URL.
        var response = await PostAsync(other, Payload(PhoneNumberId, TextMessage("wamid.x1", "hola")), AppSecret);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CountRowsAsync(other)).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task PostWebhook_ShouldPersistBoth_WhenBatchCarriesTwoMessages()
    {
        var tenant = NewTenant();
        await ConfigureTenantAsync(tenant);

        var response = await PostAsync(
            tenant,
            Payload(PhoneNumberId, TextMessage("wamid.p1", "uno"), TextMessage("wamid.p2", "dos")),
            AppSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountRowsAsync(tenant)).Should().Be((1, 1, 2));
    }

    [Fact]
    public async Task Metrics_ShouldExportRejectedCounter_WhenSignatureIsWrong()
    {
        var tenant = NewTenant();
        await ConfigureTenantAsync(tenant);
        (await PostAsync(tenant, Payload(PhoneNumberId, TextMessage("wamid.m1", "forged")), "not-the-tenant-secret"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await _client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(
            "whatsapp_webhook_rejected_total", "the WhatsApp meter is exported, so a silent tenant is visible to an operator");
    }

    // ── GET verification handshake ───────────────────────────────────────────

    [Fact]
    public async Task GetVerification_ShouldReturn403_WhenVerifyTokenMismatches()
    {
        var tenant = NewTenant();
        await ConfigureTenantAsync(tenant);

        var response = await _client.GetAsync(
            $"/api/v1/webhooks/{tenant.Value}/whatsapp?hub.mode=subscribe&hub.verify_token=guess&hub.challenge=CHALLENGE42");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("CHALLENGE42");
    }

    [Fact]
    public async Task GetVerification_ShouldEchoChallenge_WhenVerifyTokenMatches()
    {
        var tenant = NewTenant();
        await ConfigureTenantAsync(tenant);

        var response = await _client.GetAsync(
            $"/api/v1/webhooks/{tenant.Value}/whatsapp?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=CHALLENGE42");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("CHALLENGE42");
    }

    [Fact]
    public async Task GetVerification_ShouldReturn403_WhenTenantHasNoWhatsAppConfig()
    {
        var response = await _client.GetAsync(
            $"/api/v1/webhooks/{NewTenant().Value}/whatsapp?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=CHALLENGE42");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("CHALLENGE42");
    }

    [Fact]
    public async Task GetVerification_ShouldReturn403_WhenModeIsNotSubscribe()
    {
        var tenant = NewTenant();
        await ConfigureTenantAsync(tenant);

        var response = await _client.GetAsync(
            $"/api/v1/webhooks/{tenant.Value}/whatsapp?hub.mode=unsubscribe&hub.verify_token={VerifyToken}&hub.challenge=CHALLENGE42");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
