using System.Net;
using System.Net.Http.Json;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Tests;

public sealed class ManagementBillingEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;
    private readonly PlatformAdminApiFactory _factory;
    private const string TestTenantId = "billing-test-tenant";

    public ManagementBillingEndpointTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();

        // Ensure test tenant exists
        _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = TestTenantId,
            name = "Billing Test Tenant",
            type = 2, // Customer
        }).GetAwaiter().GetResult();
    }

    // ─── Rate Card Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRateCards_ShouldReturnOk()
    {
        var response = await _client.GetAsync($"/api/management/rate-cards?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateRateCard_ShouldReturnCreated()
    {
        var response = await _client.PostAsJsonAsync($"/api/management/rate-cards?tenantId={TestTenantId}", new
        {
            name = "Standard Plan",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = true,
            rates = new[]
            {
                new
                {
                    usageType = "VoiceInbound",
                    unitPrice = 0.015m,
                    includedQuantity = 100m,
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Standard Plan");
        body.Should().Contain("VoiceInbound");
    }

    [Fact]
    public async Task UpdateRateCard_ShouldReturnOk()
    {
        // Create first
        var createResponse = await _client.PostAsJsonAsync($"/api/management/rate-cards?tenantId={TestTenantId}", new
        {
            name = "Update Test",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = new[] { new { usageType = "SmsOutbound", unitPrice = 0.01m, includedQuantity = 0m } },
        });
        var created = await createResponse.Content.ReadAsStringAsync();
        var rateCardId = System.Text.Json.JsonDocument.Parse(created).RootElement.GetProperty("rateCardId").GetString();

        // Update
        var response = await _client.PutAsJsonAsync($"/api/management/rate-cards/{rateCardId}?tenantId={TestTenantId}", new
        {
            name = "Updated Plan",
            currency = "EUR",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = new[] { new { usageType = "SmsOutbound", unitPrice = 0.02m, includedQuantity = 50m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Updated Plan");
        body.Should().Contain("EUR");
    }

    [Fact]
    public async Task UpdateRateCard_ShouldReturnNotFound_WhenMissing()
    {
        var response = await _client.PutAsJsonAsync($"/api/management/rate-cards/nonexistent?tenantId={TestTenantId}", new
        {
            name = "Ghost",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = Array.Empty<object>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteRateCard_ShouldReturnNoContent()
    {
        var createResponse = await _client.PostAsJsonAsync($"/api/management/rate-cards?tenantId={TestTenantId}", new
        {
            name = "Delete Me",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = Array.Empty<object>(),
        });
        var created = await createResponse.Content.ReadAsStringAsync();
        var rateCardId = System.Text.Json.JsonDocument.Parse(created).RootElement.GetProperty("rateCardId").GetString();

        var response = await _client.DeleteAsync($"/api/management/rate-cards/{rateCardId}?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── Invoice Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListInvoices_ShouldReturnOk()
    {
        var response = await _client.GetAsync($"/api/management/invoices?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GenerateInvoice_ShouldReturnCreated()
    {
        // Seed a rate card first
        var tid = new TenantId(TestTenantId);
        var rateCardStore = _factory.Services.GetRequiredService<IRateCardStore>();
        await rateCardStore.SaveAsync(new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = tid,
            Name = "Invoice Gen Test",
            Currency = "USD",
            EffectiveFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IsDefault = true,
            Rates = [new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.01m, IncludedQuantity = 0m }],
        }, CancellationToken.None);

        // Seed usage
        var usageStore = _factory.Services.GetRequiredService<IUsageRecordStore>();
        await usageStore.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tid,
            UsageType = UsageType.VoiceInbound,
            Quantity = 100m,
            Unit = UsageUnit.Minutes,
            RecordedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        var response = await _client.PostAsJsonAsync($"/api/management/invoices/generate?tenantId={TestTenantId}", new
        {
            periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            periodEnd = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("VoiceInbound");
    }

    [Fact]
    public async Task GenerateInvoice_ShouldReturnBadRequest_WhenNoRateCard()
    {
        var response = await _client.PostAsJsonAsync("/api/management/invoices/generate?tenantId=no-rate-card-tenant", new
        {
            periodStart = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
            periodEnd = new DateTimeOffset(2099, 2, 1, 0, 0, 0, TimeSpan.Zero),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("No active rate card");
    }

    [Fact]
    public async Task GetInvoice_ShouldReturnNotFound_WhenMissing()
    {
        var response = await _client.GetAsync($"/api/management/invoices/nonexistent?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssueInvoice_ShouldReturnOk()
    {
        // Create a draft invoice via store
        var tid = new TenantId(TestTenantId);
        var invoiceId = EntityId.New();
        var invoiceStore = _factory.Services.GetRequiredService<IInvoiceStore>();
        await invoiceStore.SaveAsync(new Invoice
        {
            InvoiceId = invoiceId,
            TenantId = tid,
            PeriodStart = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            PeriodEnd = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            Currency = "USD",
            LineItems = [],
            Subtotal = 0m,
            Tax = 0m,
            Total = 0m,
            GeneratedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var response = await _client.PostAsync($"/api/management/invoices/{invoiceId.Value}/issue?tenantId={TestTenantId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Issued");
    }

    [Fact]
    public async Task IssueInvoice_ShouldReturnNotFound_WhenMissing()
    {
        var response = await _client.PostAsync($"/api/management/invoices/nonexistent/issue?tenantId={TestTenantId}", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Usage Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsageSummary_ShouldReturnOk()
    {
        var response = await _client.GetAsync($"/api/management/tenants/{TestTenantId}/usage");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetUsageDetails_ShouldReturnOk()
    {
        var response = await _client.GetAsync(
            $"/api/management/tenants/{TestTenantId}/usage/details?from=2026-01-01T00:00:00Z&until=2026-12-31T23:59:59Z");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetUsageDetails_ShouldFilterByType()
    {
        // Seed some data
        var tid = new TenantId("usage-filter-test");
        var usageStore = _factory.Services.GetRequiredService<IUsageRecordStore>();
        await usageStore.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tid,
            UsageType = UsageType.VoiceInbound,
            Quantity = 10m,
            Unit = UsageUnit.Minutes,
            RecordedAt = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        await usageStore.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tid,
            UsageType = UsageType.SmsOutbound,
            Quantity = 5m,
            Unit = UsageUnit.Segments,
            RecordedAt = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        // Ensure tenant exists
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "usage-filter-test",
            name = "Usage Filter Test",
            type = 2,
        });

        var response = await _client.GetAsync(
            "/api/management/tenants/usage-filter-test/usage/details?from=2026-06-01T00:00:00Z&until=2026-07-01T00:00:00Z&type=VoiceInbound");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("VoiceInbound");
        body.Should().NotContain("SmsOutbound");
    }

    // ─── Quota Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetQuotaStatus_ShouldReturnOk()
    {
        var response = await _client.GetAsync($"/api/management/tenants/{TestTenantId}/quota");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(TestTenantId);
    }

    [Fact]
    public async Task UpdateQuota_ShouldReturnOk()
    {
        var response = await _client.PutAsJsonAsync($"/api/management/tenants/{TestTenantId}/quota", new
        {
            maxConcurrentChannels = 200,
            maxActiveCampaigns = 20,
            maxMonthlyVoiceMinutes = 10000L,
            quotaAction = "SoftBlock",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("200");
        body.Should().Contain("SoftBlock");
    }

    // ─── Auth Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Endpoints_ShouldRequirePlatformAdmin()
    {
        using var factory = new PlatformApiFactory();
        var anonClient = factory.CreateClient();

        var response = await anonClient.GetAsync($"/api/management/rate-cards?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListInvoices_ShouldSupportPagination()
    {
        var response = await _client.GetAsync($"/api/management/invoices?tenantId={TestTenantId}&page=1&pageSize=5");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
