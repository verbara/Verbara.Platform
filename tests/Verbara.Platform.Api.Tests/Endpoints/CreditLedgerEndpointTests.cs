using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// c1 (credit-ledger-topups, ADR-0033 (c) addendum) — the sellable half of the AI-credit ledger:
/// the operator top-up mint (<c>POST /management/credit-ledger/top-up</c>, <c>PlatformAdminOnly</c> +
/// <c>billing:credits:grant</c>) and the tenant-facing balance/entries read API
/// (<c>GET /admin/credit-ledger/{balance,entries}</c>, <c>AdminOnly</c> + <c>RequireOperationalTenant</c> +
/// <c>billing:credits:read</c>). The money path (<c>PostMeteredDebitAsync</c> / invoice / quota) is
/// untouched — a top-up is a fungible <see cref="CreditSource.TopUp"/> grant via the idempotent
/// <see cref="ICreditLedgerStore.PostGrantAsync"/>.
/// </summary>
public sealed class CreditLedgerEndpointTests
{
    // ─── Operator top-up (PlatformAdmin) ──────────────────────────────────────────

    [Fact]
    public async Task TopUp_ShouldAddBalance_WhenPlatformAdminMints()
    {
        using var factory = new PlatformAdminApiFactory();
        using var client = factory.CreatePlatformAdminClient();
        var tenantId = $"topup-add-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/management/credit-ledger/top-up", new
        {
            tenantId,
            amount = 250m,
            idempotencyKey = "key-add-1",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["balance"]!.GetValue<decimal>().Should().Be(250m);

        var balance = await ReadBalanceAsync(factory, tenantId);
        balance.Should().Be(250m);
    }

    [Fact]
    public async Task TopUp_ShouldNotDoubleGrant_WhenSameIdempotencyKeyReposted()
    {
        using var factory = new PlatformAdminApiFactory();
        using var client = factory.CreatePlatformAdminClient();
        var tenantId = $"topup-idem-{Guid.NewGuid():N}";

        var first = await client.PostAsJsonAsync("/api/management/credit-ledger/top-up", new
        {
            tenantId,
            amount = 100m,
            idempotencyKey = "dup-key",
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync("/api/management/credit-ledger/top-up", new
        {
            tenantId,
            amount = 100m,
            idempotencyKey = "dup-key",
        });
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        // Idempotent on external_ref: the second mint with the same key is a no-op — balance stays 100.
        var json = JsonNode.Parse(await second.Content.ReadAsStringAsync());
        json!["balance"]!.GetValue<decimal>().Should().Be(100m);
        (await ReadBalanceAsync(factory, tenantId)).Should().Be(100m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task TopUp_ShouldReturnBadRequest_WhenAmountNonPositive(decimal amount)
    {
        using var factory = new PlatformAdminApiFactory();
        using var client = factory.CreatePlatformAdminClient();

        var response = await client.PostAsJsonAsync("/api/management/credit-ledger/top-up", new
        {
            tenantId = "topup-bad",
            amount,
            idempotencyKey = "key-bad",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TopUp_ShouldForbid_WhenCallerIsTenantAdminWithoutGrant()
    {
        // A Customer-tenant admin (no management key) cannot reach the operator mint surface:
        // PlatformAdminOnly rejects before the billing:credits:grant route gate is even evaluated.
        using var factory = new AuthenticatedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/management/credit-ledger/top-up", new
        {
            tenantId = AuthenticatedPlatformApiFactory.TestTenantId,
            amount = 50m,
            idempotencyKey = "key-forbidden",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── Tenant read (AdminOnly + operational tenant) ─────────────────────────────

    [Fact]
    public async Task GetBalance_ShouldReturnCallerTenantBalance_WhenSeeded()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await SeedGrantAsync(factory, AuthenticatedPlatformApiFactory.TestTenantId, 175m, "seed-bal");

        var response = await client.GetAsync("/api/admin/credit-ledger/balance");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["balance"]!.GetValue<decimal>().Should().Be(175m);
    }

    [Fact]
    public async Task GetEntries_ShouldReturnPagedResult_WithAccurateTotals()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var tenantId = AuthenticatedPlatformApiFactory.TestTenantId;

        // Seed 5 distinct TopUp grants (distinct external_ref so each inserts).
        for (var i = 0; i < 5; i++)
            await SeedGrantAsync(factory, tenantId, 10m, $"entry-{i}");

        var response = await client.GetAsync("/api/admin/credit-ledger/entries?page=1&pageSize=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["totalCount"]!.GetValue<int>().Should().Be(5);
        json["totalPages"]!.GetValue<int>().Should().Be(3); // ceil(5 / 2)
        json["page"]!.GetValue<int>().Should().Be(1);
        json["pageSize"]!.GetValue<int>().Should().Be(2);
        json["items"]!.AsArray().Should().HaveCount(2);

        // Entry projection surfaces the DTO shape (enum names, external_ref).
        var first = json["items"]!.AsArray()[0]!;
        first["entryType"]!.GetValue<string>().Should().Be("Grant");
        first["source"]!.GetValue<string>().Should().Be("TopUp");
        first["amount"]!.GetValue<decimal>().Should().Be(10m);
    }

    [Fact]
    public async Task GetEntries_ShouldClampPageSize_WhenOversized()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await SeedGrantAsync(factory, AuthenticatedPlatformApiFactory.TestTenantId, 5m, "clamp-1");

        var response = await client.GetAsync("/api/admin/credit-ledger/entries?page=0&pageSize=9999");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["page"]!.GetValue<int>().Should().Be(1);    // page<1 → 1
        json["pageSize"]!.GetValue<int>().Should().Be(200); // >200 → 200
    }

    // ─── credit-grant-lazy-mint-rollover — the readout balance path's inline mint-on-read ─────────────────

    [Fact]
    public async Task GetBalance_ShouldLazyMintCurrentPeriodGrant_WhenAiCreditsMonthlyConfigured_AndNoGrantYet()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var tenantId = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId);

        await SeedQuotaAsync(factory, tenantId, aiCreditsMonthly: 750L);

        var response = await client.GetAsync("/api/admin/credit-ledger/balance");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        // No current-period grant existed before this read; the lazy mint posts it inline so the FIRST read
        // already observes the allowance (the credit-grant-lazy-mint-rollover fast-follow).
        json!["balance"]!.GetValue<decimal>().Should().Be(750m);

        // Steady state: a second read must NOT double-mint (idempotent on period_key) — balance stays 750.
        var second = await client.GetAsync("/api/admin/credit-ledger/balance");
        var secondJson = JsonNode.Parse(await second.Content.ReadAsStringAsync());
        secondJson!["balance"]!.GetValue<decimal>().Should().Be(750m);
    }

    [Fact]
    public async Task GetBalance_ShouldNotMint_WhenAiCreditsMonthlyIsNull()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var tenantId = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId);

        await SeedQuotaAsync(factory, tenantId, aiCreditsMonthly: null);

        var response = await client.GetAsync("/api/admin/credit-ledger/balance");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["balance"]!.GetValue<decimal>().Should().Be(0m);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────────

    private static async Task<decimal> ReadBalanceAsync(WebApplicationFactory<Program> factory, string tenantId)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ICreditLedgerStore>();
        return await ledger.GetBalanceAsync(new TenantId(tenantId), CancellationToken.None);
    }

    private static async Task SeedQuotaAsync(WebApplicationFactory<Program> factory, TenantId tenantId, long? aiCreditsMonthly)
    {
        using var scope = factory.Services.CreateScope();
        var quotaStore = scope.ServiceProvider.GetRequiredService<ITenantQuotaStore>();
        await quotaStore.UpsertAsync(new TenantQuota
        {
            TenantId = tenantId,
            AiCreditsMonthly = aiCreditsMonthly,
            QuotaAction = QuotaAction.Warn,
        }, CancellationToken.None);
    }

    private static async Task SeedGrantAsync(
        WebApplicationFactory<Program> factory, string tenantId, decimal amount, string externalRef)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ICreditLedgerStore>();
        await ledger.PostGrantAsync(new CreditLedgerEntry
        {
            EntryId = EntityId.New(),
            TenantId = new TenantId(tenantId),
            EntryType = CreditEntryType.Grant,
            Source = CreditSource.TopUp,
            Amount = amount,
            ExternalRef = externalRef,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }
}
