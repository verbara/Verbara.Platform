using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

public sealed class GdprEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _adminClient;
    private readonly PlatformAdminApiFactory _factory;

    public GdprEndpointTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _adminClient = factory.CreatePlatformAdminClient();
    }

    // ─── Export ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ShouldReturnOk_WhenContactExists()
    {
        // Seed a contact
        var contactStore = _factory.Services.GetRequiredService<IContactStore>();
        var tenantId = new TenantId(PlatformAdminApiFactory.HostTenantId);
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            FirstName = "GDPR",
            LastName = "Test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await contactStore.SaveAsync(contact, CancellationToken.None);

        var response = await _adminClient.PostAsJsonAsync(
            $"/api/admin/gdpr/export",
            new { contactId = contact.ContactId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("exportId");
        body.Should().Contain(contact.ContactId.Value);
    }

    [Fact]
    public async Task Export_ShouldReturnOk_WhenContactHasNoData()
    {
        var response = await _adminClient.PostAsJsonAsync(
            "/api/admin/gdpr/export",
            new { contactId = "nonexistent-contact" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("exportId");
    }

    [Fact]
    public async Task Export_ShouldReturnBadRequest_WhenContactIdMissing()
    {
        var response = await _adminClient.PostAsJsonAsync(
            "/api/admin/gdpr/export",
            new { contactId = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Purge ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_ShouldReturnOk_AndCreateTombstone()
    {
        // Seed a contact
        var contactStore = _factory.Services.GetRequiredService<IContactStore>();
        var tenantId = new TenantId(PlatformAdminApiFactory.HostTenantId);
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            FirstName = "Purge",
            LastName = "Subject",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await contactStore.SaveAsync(contact, CancellationToken.None);

        var response = await _adminClient.PostAsJsonAsync(
            "/api/admin/gdpr/purge",
            new { contactId = contact.ContactId.Value, reason = "Subject erasure request" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("purgeId");
        body.Should().Contain("contact");
    }

    [Fact]
    public async Task Purge_ShouldReturnBadRequest_WhenReasonMissing()
    {
        var response = await _adminClient.PostAsJsonAsync(
            "/api/admin/gdpr/purge",
            new { contactId = "some-contact", reason = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Purge Log ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeLog_ShouldReturnOk()
    {
        var response = await _adminClient.GetAsync("/api/management/gdpr/purge-log");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Retention Policy ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetRetentionPolicy_ShouldReturnDefaults_WhenNotConfigured()
    {
        var response = await _adminClient.GetAsync(
            $"/api/management/tenants/{PlatformAdminApiFactory.HostTenantId}/retention");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(PlatformAdminApiFactory.HostTenantId);
    }

    [Fact]
    public async Task UpdateRetentionPolicy_ShouldReturnOk()
    {
        var response = await _adminClient.PutAsJsonAsync(
            $"/api/management/tenants/{PlatformAdminApiFactory.HostTenantId}/retention",
            new
            {
                conversationRetentionDays = 90,
                authEventRetentionDays = 365,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("90");
        body.Should().Contain("365");
    }

    [Fact]
    public async Task UpdateRetentionPolicy_ShouldPersistAndRetrieve()
    {
        await _adminClient.PutAsJsonAsync(
            $"/api/management/tenants/{PlatformAdminApiFactory.HostTenantId}/retention",
            new { conversationRetentionDays = 60, auditRetentionDays = 180 });

        var getResponse = await _adminClient.GetAsync(
            $"/api/management/tenants/{PlatformAdminApiFactory.HostTenantId}/retention");
        var body = await getResponse.Content.ReadAsStringAsync();

        body.Should().Contain("60");
        body.Should().Contain("180");
    }
}
