using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Audit;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Regression suite for <c>PREPUB-2026-05-09-BILL-001</c> (8 money-mutating
/// billing handlers must emit IAuditService entries) and
/// <c>PREPUB-2026-05-09-BILL-002</c> (PayInvoice must validate tenant ownership
/// + invoice id shape). Pairs with <see cref="ManagementBillingEndpointTests"/>
/// which covers the happy-path responses.
/// </summary>
public sealed class ManagementBillingAuditTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;
    private const string TestTenantId = "billing-audit-tenant";

    public ManagementBillingAuditTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();

        // Ensure tenant exists once.
        _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = TestTenantId,
            name = "Billing Audit Tenant",
            type = 2, // Customer
        }).GetAwaiter().GetResult();
    }

    // ─── BILL-001: each mutating handler must emit an audit entry ────────────

    [Fact]
    public async Task CreateRateCard_ShouldEmitAudit_WhenSucceeds()
    {
        var response = await _client.PostAsJsonAsync($"/api/management/rate-cards?tenantId={TestTenantId}", new
        {
            name = "Audit Create Rate Card",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = new[] { new { usageType = "VoiceInbound", unitPrice = 0.01m, includedQuantity = 0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await AssertAuditEmittedAsync(BillingAuditEventTypes.RateCardCreated, "warning");
    }

    [Fact]
    public async Task UpdateRateCard_ShouldEmitAudit_WhenSucceeds()
    {
        var create = await _client.PostAsJsonAsync($"/api/management/rate-cards?tenantId={TestTenantId}", new
        {
            name = "To Update",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = new[] { new { usageType = "SmsOutbound", unitPrice = 0.01m, includedQuantity = 0m } },
        });
        var rateCardId = System.Text.Json.JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("rateCardId").GetString();

        var response = await _client.PutAsJsonAsync($"/api/management/rate-cards/{rateCardId}?tenantId={TestTenantId}", new
        {
            name = "Updated Audit",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = new[] { new { usageType = "SmsOutbound", unitPrice = 0.02m, includedQuantity = 0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertAuditEmittedAsync(BillingAuditEventTypes.RateCardUpdated, "warning");
    }

    [Fact]
    public async Task DeleteRateCard_ShouldEmitAudit_WhenSucceeds()
    {
        var create = await _client.PostAsJsonAsync($"/api/management/rate-cards?tenantId={TestTenantId}", new
        {
            name = "To Delete Audit",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = Array.Empty<object>(),
        });
        var rateCardId = System.Text.Json.JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("rateCardId").GetString();

        var response = await _client.DeleteAsync($"/api/management/rate-cards/{rateCardId}?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertAuditEmittedAsync(BillingAuditEventTypes.RateCardDeleted, "critical");
    }

    [Fact]
    public async Task GenerateInvoice_ShouldEmitAudit_WhenSucceeds()
    {
        // Seed a rate card and usage so the generator returns successfully.
        var tid = new TenantId(TestTenantId);
        var rateCardStore = _factory.Services.GetRequiredService<IRateCardStore>();
        await rateCardStore.SaveAsync(new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = tid,
            Name = "Generate Audit Card",
            Currency = "USD",
            EffectiveFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IsDefault = true,
            Rates = [new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.01m, IncludedQuantity = 0m }],
        }, CancellationToken.None);
        var usageStore = _factory.Services.GetRequiredService<IUsageRecordStore>();
        await usageStore.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tid,
            UsageType = UsageType.VoiceInbound,
            Quantity = 50m,
            Unit = UsageUnit.Minutes,
            RecordedAt = new DateTimeOffset(2026, 4, 15, 12, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        var response = await _client.PostAsJsonAsync($"/api/management/invoices/generate?tenantId={TestTenantId}", new
        {
            periodStart = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            periodEnd = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await AssertAuditEmittedAsync(BillingAuditEventTypes.InvoiceGenerated, "warning");
    }

    [Fact]
    public async Task IssueInvoice_ShouldEmitAudit_WhenSucceeds()
    {
        var tid = new TenantId(TestTenantId);
        var invoiceId = EntityId.New();
        var invoiceStore = _factory.Services.GetRequiredService<IInvoiceStore>();
        await invoiceStore.SaveAsync(new Invoice
        {
            InvoiceId = invoiceId,
            TenantId = tid,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-30),
            PeriodEnd = DateTimeOffset.UtcNow,
            Currency = "USD",
            LineItems = [],
            Subtotal = 0m,
            Tax = 0m,
            Total = 0m,
            GeneratedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var response = await _client.PostAsync(
            $"/api/management/invoices/{invoiceId.Value}/issue?tenantId={TestTenantId}", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertAuditEmittedAsync(BillingAuditEventTypes.InvoiceIssued, "warning");
    }

    [Fact]
    public async Task UpdateQuota_ShouldEmitAudit_WhenSucceeds()
    {
        var response = await _client.PutAsJsonAsync($"/api/management/tenants/{TestTenantId}/quota", new
        {
            maxConcurrentChannels = 250,
            maxActiveCampaigns = 25,
            quotaAction = "Warn",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertAuditEmittedAsync(BillingAuditEventTypes.TenantQuotaUpdated, "warning");
    }

    [Fact]
    public async Task PauseDunning_ShouldEmitAudit_WhenSucceeds()
    {
        const string tenant = "billing-audit-pause-tenant";
        await SeedDunningRecordAsync(tenant, isPaused: false);

        var response = await _client.PostAsync(
            $"/api/management/tenants/{tenant}/dunning/pause", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertAuditEmittedAsync(BillingAuditEventTypes.DunningPaused, "warning", tenantId: tenant);
    }

    [Fact]
    public async Task PayInvoice_ShouldRecordAudit_WhenSucceeds()
    {
        const string tenant = "billing-audit-pay-tenant";
        var invoiceId = await SeedInvoiceWithDunningAsync(tenant);

        var response = await _client.PostAsync(
            $"/api/management/invoices/{invoiceId}/pay?tenantId={tenant}", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertAuditEmittedAsync(BillingAuditEventTypes.InvoicePaid, "warning", tenantId: tenant);
    }

    // ─── BILL-002: PayInvoice cross-check + invoiceId validation ─────────────

    [Fact]
    public async Task PayInvoice_ShouldReject400_WhenTenantIdMismatchesDunningRecord()
    {
        const string realTenant = "billing-pay-real-tenant";
        const string spoofedTenant = "billing-pay-spoofed-tenant";
        var invoiceId = await SeedInvoiceWithDunningAsync(realTenant);

        var response = await _client.PostAsync(
            $"/api/management/invoices/{invoiceId}/pay?tenantId={spoofedTenant}", content: null);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            because: "BILL-002: ?tenantId must match the dunning record's tenant — defends against cross-tenant pay attempts");
        await AssertAuditEmittedAsync(BillingAuditEventTypes.InvoicePayAttempted, "warning", tenantId: spoofedTenant);
    }

    [Fact]
    public async Task PayInvoice_ShouldReject400_WhenInvoiceIdNotValid()
    {
        // Whitespace-only id is rejected by EntityId.IsValid.
        const string whitespaceId = "%20%20"; // url-encoded "  "
        var response = await _client.PostAsync(
            $"/api/management/invoices/{whitespaceId}/pay?tenantId={TestTenantId}", content: null);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            because: "BILL-002: malformed invoiceId must fail fast before reaching the store layer");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task AssertAuditEmittedAsync(string action, string expectedSeverity, string? tenantId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var auditStore = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await auditStore.SearchAsync(
            new TenantId(tenantId ?? TestTenantId),
            new AuditQuery(Action: action, Page: 1, PageSize: 50),
            CancellationToken.None);

        hits.Items.Should().NotBeEmpty($"audit entry with action='{action}' must be emitted by the handler");
        hits.Items[0].Category.Should().Be("billing");
        hits.Items[0].Severity.Should().Be(expectedSeverity);
    }

    private async Task<string> SeedInvoiceWithDunningAsync(string tenant)
    {
        // Tenant must exist so the PayInvoice -> tenantStore.UpdateStatusAsync works.
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = tenant,
            name = $"Billing Pay Tenant {tenant}",
            type = 2,
        });

        var tid = new TenantId(tenant);
        var invoiceId = EntityId.New();
        using var scope = _factory.Services.CreateScope();
        var invoiceStore = scope.ServiceProvider.GetRequiredService<IInvoiceStore>();
        var dunningStore = scope.ServiceProvider.GetRequiredService<IDunningStore>();

        await invoiceStore.SaveAsync(new Invoice
        {
            InvoiceId = invoiceId,
            TenantId = tid,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-30),
            PeriodEnd = DateTimeOffset.UtcNow,
            Currency = "USD",
            LineItems = [],
            Subtotal = 0m,
            Tax = 0m,
            Total = 0m,
            GeneratedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        await dunningStore.UpsertAsync(new DunningRecord
        {
            DunningId = $"dunning-{tenant}",
            TenantId = tenant,
            InvoiceId = invoiceId.Value,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-3),
            IsActive = true,
        }, CancellationToken.None);

        return invoiceId.Value;
    }

    private async Task SeedDunningRecordAsync(string tenant, bool isPaused)
    {
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = tenant,
            name = $"Billing Dunning Tenant {tenant}",
            type = 2,
        });

        using var scope = _factory.Services.CreateScope();
        var dunningStore = scope.ServiceProvider.GetRequiredService<IDunningStore>();
        await dunningStore.UpsertAsync(new DunningRecord
        {
            DunningId = $"dunning-{tenant}",
            TenantId = tenant,
            InvoiceId = $"invoice-{tenant}",
            StartedAt = DateTimeOffset.UtcNow.AddDays(-3),
            IsPaused = isPaused,
            IsActive = true,
        }, CancellationToken.None);
    }
}
