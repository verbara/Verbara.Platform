using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// c2 (credit-ledger-lots, ADR-0033 (c2) addendum) — operator Promo/Partner grants (PlatformAdminOnly +
/// billing:credits:grant double-lock), tenant-facing per-source remaining (AdminOnly + RequireOperationalTenant +
/// billing:credits:read), and the partner derive-on-read attribution (PartnerAdminOnly). Partner-funded draws are
/// tagged <see cref="CreditSource.Partner"/> (never PostPaid) so they stay attributable without ever entering the
/// customer-owed invoice — attribution is computed on read from the ledger + the single-hop tenant hierarchy.
/// </summary>
public sealed class CreditLedgerGrantsEndpointTests
{
    // ─── Promo grant (PlatformAdmin + billing:credits:grant) ──────────────────────

    [Fact]
    public async Task PromoGrant_ShouldMintPromoLot_WhenPlatformAdminMints()
    {
        using var factory = new CreditLedgerGrantsApiFactory();
        using var client = factory.CreateGrantClient();
        var tenantId = CreditLedgerGrantsApiFactory.CustomerUnderPartnerId;

        var response = await client.PostAsJsonAsync("/api/management/credit-ledger/promo-grant", new
        {
            tenantId,
            amount = 100m,
            idempotencyKey = "promo-mint-1",
            expiresAt = (DateTimeOffset?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["balance"]!.GetValue<decimal>().Should().Be(100m);

        // The grant minted exactly one Promo lot.
        var lots = await GetLotsAsync(factory, tenantId);
        lots.Should().ContainSingle(l => l.Source == CreditSource.Promo && l.Original == 100m);
    }

    [Fact]
    public async Task PromoGrant_ShouldNotDoubleGrant_WhenSameIdempotencyKeyReposted()
    {
        using var factory = new CreditLedgerGrantsApiFactory();
        using var client = factory.CreateGrantClient();
        var tenantId = CreditLedgerGrantsApiFactory.CustomerUnderPartnerId;

        await client.PostAsJsonAsync("/api/management/credit-ledger/promo-grant", new
        {
            tenantId, amount = 100m, idempotencyKey = "promo-dup", expiresAt = (DateTimeOffset?)null,
        });
        var second = await client.PostAsJsonAsync("/api/management/credit-ledger/promo-grant", new
        {
            tenantId, amount = 100m, idempotencyKey = "promo-dup", expiresAt = (DateTimeOffset?)null,
        });

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await second.Content.ReadAsStringAsync());
        json!["balance"]!.GetValue<decimal>().Should().Be(100m); // no double-credit

        (await GetLotsAsync(factory, tenantId)).Count(l => l.Source == CreditSource.Promo).Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task PromoGrant_ShouldReturnBadRequest_WhenAmountNonPositive(decimal amount)
    {
        using var factory = new CreditLedgerGrantsApiFactory();
        using var client = factory.CreateGrantClient();

        var response = await client.PostAsJsonAsync("/api/management/credit-ledger/promo-grant", new
        {
            tenantId = CreditLedgerGrantsApiFactory.CustomerUnderPartnerId,
            amount,
            idempotencyKey = "promo-bad",
            expiresAt = (DateTimeOffset?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PromoGrant_ShouldForbid_WhenManagementKeyLacksGrantPermission()
    {
        using var factory = new CreditLedgerGrantsApiFactory();
        using var client = factory.CreateNoGrantClient(); // scope = audit.read only

        var response = await client.PostAsJsonAsync("/api/management/credit-ledger/promo-grant", new
        {
            tenantId = CreditLedgerGrantsApiFactory.CustomerUnderPartnerId,
            amount = 100m,
            idempotencyKey = "promo-nogrant",
            expiresAt = (DateTimeOffset?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── Partner grant (PlatformAdmin + billing:credits:grant + parent-is-Partner gate) ──

    [Fact]
    public async Task PartnerGrant_ShouldMintPartnerLot_WhenTenantParentIsPartner()
    {
        using var factory = new CreditLedgerGrantsApiFactory();
        using var client = factory.CreateGrantClient();
        var tenantId = CreditLedgerGrantsApiFactory.CustomerUnderPartnerId;

        var response = await client.PostAsJsonAsync("/api/management/credit-ledger/partner-grant", new
        {
            tenantId, amount = 1000m, idempotencyKey = "partner-mint-1",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["balance"]!.GetValue<decimal>().Should().Be(1000m);

        (await GetLotsAsync(factory, tenantId))
            .Should().ContainSingle(l => l.Source == CreditSource.Partner && l.Original == 1000m);
    }

    [Fact]
    public async Task PartnerGrant_ShouldReturnBadRequest_WhenParentIsNotPartner()
    {
        using var factory = new CreditLedgerGrantsApiFactory();
        using var client = factory.CreateGrantClient();

        // CustomerUnderPlatform's parent is the Platform tenant, not a Partner → rejected.
        var response = await client.PostAsJsonAsync("/api/management/credit-ledger/partner-grant", new
        {
            tenantId = CreditLedgerGrantsApiFactory.CustomerUnderPlatformId,
            amount = 1000m,
            idempotencyKey = "partner-bad-parent",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PartnerGrant_ShouldNotDoubleGrant_WhenSameIdempotencyKeyReposted()
    {
        using var factory = new CreditLedgerGrantsApiFactory();
        using var client = factory.CreateGrantClient();
        var tenantId = CreditLedgerGrantsApiFactory.CustomerUnderPartnerId;

        await client.PostAsJsonAsync("/api/management/credit-ledger/partner-grant", new
        {
            tenantId, amount = 500m, idempotencyKey = "partner-dup",
        });
        var second = await client.PostAsJsonAsync("/api/management/credit-ledger/partner-grant", new
        {
            tenantId, amount = 500m, idempotencyKey = "partner-dup",
        });

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await second.Content.ReadAsStringAsync());
        json!["balance"]!.GetValue<decimal>().Should().Be(500m);
        (await GetLotsAsync(factory, tenantId)).Count(l => l.Source == CreditSource.Partner).Should().Be(1);
    }

    // ─── Per-source remaining (AdminOnly + operational tenant + billing:credits:read) ──

    [Fact]
    public async Task GetRemainingBySource_ShouldReturnPerSourceBreakdown_SummingToBalance()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var tenantId = AuthenticatedPlatformApiFactory.TestTenantId;

        await SeedGrantAsync(factory, tenantId, 200m, CreditSource.Promo, "rbs-promo");
        await SeedGrantAsync(factory, tenantId, 800m, CreditSource.TopUp, "rbs-topup");

        var response = await client.GetAsync("/api/admin/credit-ledger/remaining-by-source");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var sources = json!["sources"]!.AsArray();

        decimal Remaining(string name) => sources
            .First(s => s!["source"]!.GetValue<string>() == name)!["remaining"]!.GetValue<decimal>();
        Remaining("Promo").Should().Be(200m);
        Remaining("TopUp").Should().Be(800m);

        // The Σ over the breakdown reconciles to the O(1) balance projection.
        var total = sources.Sum(s => s!["remaining"]!.GetValue<decimal>());
        total.Should().Be(await ReadBalanceAsync(factory, tenantId));
    }

    // ─── Partner derive-on-read attribution (PartnerAdminOnly) ─────────────────────

    [Fact]
    public async Task GetPartnerAttribution_ShouldSumPartnerConsumption_WithNoCustomerInvoice()
    {
        using var factory = new PartnerApiFactory();
        using var client = factory.CreatePartnerClient();
        var customerId = PartnerApiFactory.CustomerTenantId;

        // Spec scenario: 1000 Partner grant onto the customer, consume 600 → attribution 600, customer-owed 0.
        using (var scope = factory.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<ICreditLedgerStore>();
            await ledger.PostGrantAsync(new CreditLedgerEntry
            {
                EntryId = EntityId.New(),
                TenantId = new TenantId(customerId),
                EntryType = CreditEntryType.Grant,
                Source = CreditSource.Partner,
                Amount = 1000m,
                ExternalRef = "attr-partner-grant",
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
            // 600 draws entirely from the Partner lot → Partner-tagged covered debit, no PostPaid tail.
            await ledger.PostMeteredDebitAsync(new TenantId(customerId), 600m, "attr-usage", CancellationToken.None);
        }

        var response = await client.GetAsync("/api/partner/credit-ledger/attribution");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["total"]!.GetValue<decimal>().Should().Be(600m);

        var line = json["customers"]!.AsArray()
            .First(c => c!["customerTenantId"]!.GetValue<string>() == customerId);
        line!["credits"]!.GetValue<decimal>().Should().Be(600m);

        // No invoice involved: there are no PostPaid debits, so customer-owed is 0.
        using var verifyScope = factory.Services.CreateScope();
        var verifyLedger = verifyScope.ServiceProvider.GetRequiredService<ICreditLedgerStore>();
        var postPaid = await verifyLedger.GetPostPaidDebitsTotalAsync(
            new TenantId(customerId),
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        postPaid.Should().Be(0m);
    }

    [Fact]
    public async Task GetPartnerAttribution_ShouldUseVerifiedClaim_WhenXTenantIdSpoofed()
    {
        // Security regression: the attribution partner id MUST come from the verified JWT claim, NOT the
        // resolved Items["TenantId"] (which TenantBoundaryValidationMiddleware lets a Partner override via
        // X-Tenant-Id). A partner spoofing X-Tenant-Id to its own customer must STILL read its OWN attribution
        // (the partner's children), never the spoofed tenant's. If the endpoint read Items, GetChildrenAsync on
        // the customer would return [] → total 0; the verified-claim path keeps it at the real 600.
        using var factory = new PartnerApiFactory();
        var customerId = PartnerApiFactory.CustomerTenantId;

        using (var scope = factory.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<ICreditLedgerStore>();
            await ledger.PostGrantAsync(new CreditLedgerEntry
            {
                EntryId = EntityId.New(),
                TenantId = new TenantId(customerId),
                EntryType = CreditEntryType.Grant,
                Source = CreditSource.Partner,
                Amount = 1000m,
                ExternalRef = "attr-spoof-grant",
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
            await ledger.PostMeteredDebitAsync(new TenantId(customerId), 600m, "attr-spoof-usage", CancellationToken.None);
        }

        // Authenticate as the partner but spoof X-Tenant-Id to the customer tenant (which a Partner is permitted
        // to set). The verified tenant_id claim stays PartnerTenantId.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {PartnerApiFactory.TestPartnerApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", customerId);

        var response = await client.GetAsync("/api/partner/credit-ledger/attribution");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        // Still the PARTNER's own attribution (600), proving the X-Tenant-Id override did not redirect it.
        json!["total"]!.GetValue<decimal>().Should().Be(600m);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────────

    private static async Task<decimal> ReadBalanceAsync(WebApplicationFactory<Program> factory, string tenantId)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ICreditLedgerStore>();
        return await ledger.GetBalanceAsync(new TenantId(tenantId), CancellationToken.None);
    }

    private static async Task<IReadOnlyList<CreditLot>> GetLotsAsync(
        WebApplicationFactory<Program> factory, string tenantId)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ICreditLedgerStore>();
        return await ledger.GetLotsAsync(new TenantId(tenantId), CancellationToken.None);
    }

    private static async Task SeedGrantAsync(
        WebApplicationFactory<Program> factory, string tenantId, decimal amount, CreditSource source, string externalRef)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ICreditLedgerStore>();
        await ledger.PostGrantAsync(new CreditLedgerEntry
        {
            EntryId = EntityId.New(),
            TenantId = new TenantId(tenantId),
            EntryType = CreditEntryType.Grant,
            Source = source,
            Amount = amount,
            ExternalRef = externalRef,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }
}
