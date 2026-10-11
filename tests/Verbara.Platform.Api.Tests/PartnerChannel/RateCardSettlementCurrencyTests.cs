using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.PartnerChannel;

/// <summary>
/// partner-channel-pilot-fixes P2 (rate-card-settlement-currency, design D2/D3): every rate-card write takes an
/// ISO 4217 code only, canonicalised to upper case, and a Partner invoice is refused before anything is written
/// when the Partner card and the host base card disagree on the currency, when either stored currency is not
/// ISO 4217, or when the host base card is missing.
/// </summary>
public sealed class RateCardSettlementCurrencyTests : IClassFixture<PartnerApiFactory>, IClassFixture<PlatformAdminApiFactory>
{
    private const string MgmtTenant = "currency-mgmt-tenant";
    private readonly PartnerApiFactory _partner;
    private readonly PlatformAdminApiFactory _platform;

    public RateCardSettlementCurrencyTests(PartnerApiFactory partner, PlatformAdminApiFactory platform)
    {
        _partner = partner;
        _platform = platform;
    }

    private static object CardBody(string currency, string name = "Currency Card") => new
    {
        name,
        currency,
        effectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        effectiveTo = (DateTimeOffset?)null,
        isDefault = false,
        rates = new[] { new { usageType = "VoiceInbound", unitPrice = 0.01m, includedQuantity = 0m } },
    };

    private static JsonObject InvalidBody => PartnerChannelTestKit.FixtureBody("rate-card-currency-refusals.json", "invalidCurrency");

    private static async Task<string> RateCardIdAsync(HttpResponseMessage response)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return json["rateCardId"]!.GetValue<string>();
    }

    [Theory]
    [InlineData("partner", "banana")]
    [InlineData("partner", "")]
    [InlineData("partner", "XYZ123")]
    [InlineData("partner", "XYZ")]
    [InlineData("management", "banana")]
    [InlineData("management", "")]
    [InlineData("management", "XYZ123")]
    [InlineData("management", "XYZ")]
    public async Task CreateRateCard_ShouldReturn400CurrencyInvalid_WhenCurrencyIsNotIso4217(string path, string currency)
    {
        var name = $"invalid-{path}-{Guid.NewGuid():N}";
        HttpResponseMessage response;
        IRateCardStore store;
        TenantId owner;
        if (path == "partner")
        {
            response = await _partner.CreatePartnerClient().PostAsJsonAsync("/api/v1/partner/rate-cards", CardBody(currency, name));
            store = _partner.RateCardStore;
            owner = new TenantId(PartnerApiFactory.PartnerTenantId);
        }
        else
        {
            response = await _platform.CreatePlatformAdminClient()
                .PostAsJsonAsync($"/api/management/rate-cards?tenantId={MgmtTenant}", CardBody(currency, name));
            store = _platform.Services.GetRequiredService<IRateCardStore>();
            owner = new TenantId(MgmtTenant);
        }

        var body = await PartnerChannelTestKit.AssertRefusalAsync(response, HttpStatusCode.BadRequest, InvalidBody, "currency-invalid");
        body["currency"]!.GetValue<string>().Should().Be(currency);
        (await store.ListAsync(owner, CancellationToken.None)).Should().NotContain(c => c.Name == name, "nothing is saved");
    }

    [Theory]
    [InlineData("partner")]
    [InlineData("management")]
    public async Task UpdateRateCard_ShouldReturn400CurrencyInvalid_WhenCurrencyIsNotIso4217(string path)
    {
        HttpClient client;
        string collection;
        IRateCardStore store;
        TenantId owner;
        if (path == "partner")
        {
            client = _partner.CreatePartnerClient();
            collection = "/api/v1/partner/rate-cards";
            store = _partner.RateCardStore;
            owner = new TenantId(PartnerApiFactory.PartnerTenantId);
        }
        else
        {
            client = _platform.CreatePlatformAdminClient();
            collection = "/api/management/rate-cards";
            store = _platform.Services.GetRequiredService<IRateCardStore>();
            owner = new TenantId(MgmtTenant);
        }

        var query = path == "partner" ? "" : $"?tenantId={MgmtTenant}";
        var created = await client.PostAsJsonAsync(collection + query, CardBody("USD", "Before Update"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await RateCardIdAsync(created);

        var response = await client.PutAsJsonAsync($"{collection}/{id}{query}", CardBody("banana", "After Update"));

        var body = await PartnerChannelTestKit.AssertRefusalAsync(response, HttpStatusCode.BadRequest, InvalidBody, "currency-invalid");
        body["currency"]!.GetValue<string>().Should().Be("banana");
        var stored = await store.GetByIdAsync(owner, EntityId.From(id), CancellationToken.None);
        stored!.Name.Should().Be("Before Update", "the stored card is unchanged");
        stored.Currency.Should().Be("USD");
    }

    [Theory]
    [InlineData("partner")]
    [InlineData("management")]
    public async Task CreateRateCard_ShouldStoreCanonicalCode_WhenCurrencyIsLowerCaseWithSpaces(string path)
    {
        var response = path == "partner"
            ? await _partner.CreatePartnerClient().PostAsJsonAsync("/api/v1/partner/rate-cards", CardBody(" usd "))
            : await _platform.CreatePlatformAdminClient()
                .PostAsJsonAsync($"/api/management/rate-cards?tenantId={MgmtTenant}", CardBody(" usd "));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["currency"]!.GetValue<string>().Should().Be("USD");
    }

    // ─── Partner invoice generation ────────────────────────────────────────────────

    private static async Task SeedCardAsync(PartnerApiFactory factory, string tenantId, string currency, decimal unitPrice)
    {
        await factory.RateCardStore.SaveAsync(new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = new TenantId(tenantId),
            Name = $"{tenantId} card",
            Currency = currency,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-30),
            IsDefault = true,
            Rates = [new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = unitPrice }],
        }, CancellationToken.None);
    }

    private static Task<HttpResponseMessage> GenerateAsync(PartnerApiFactory factory)
    {
        var now = DateTimeOffset.UtcNow;
        var from = Uri.EscapeDataString(now.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.ToString("O"));
        return factory.CreatePartnerClient().PostAsync(
            $"/api/v1/partner/customers/{PartnerApiFactory.CustomerTenantId}/invoices/generate?from={from}&to={to}", null);
    }

    private static async Task AssertNothingWrittenAsync(PartnerApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceStore>();
        (await invoices.ListAsync(new TenantId(PartnerApiFactory.CustomerTenantId), 1, 50, CancellationToken.None))
            .Should().BeEmpty("a refused invoice generation leaves no invoice behind");
        (await factory.RevenueStore.ListAsync(new TenantId(PartnerApiFactory.PartnerTenantId), null, null, CancellationToken.None))
            .Should().BeEmpty("and no partner-revenue record");
    }

    [Fact]
    public async Task GenerateCustomerInvoice_ShouldReturn400CurrencyMismatch_WhenPartnerAndHostCurrenciesDiffer()
    {
        await using var factory = new PartnerApiFactory();
        _ = factory.Server;
        await SeedCardAsync(factory, PartnerApiFactory.HostTenantId, "USD", 0.05m);
        await SeedCardAsync(factory, PartnerApiFactory.PartnerTenantId, "PEN", 0.20m);

        var response = await GenerateAsync(factory);

        var body = await PartnerChannelTestKit.AssertRefusalAsync(response, HttpStatusCode.BadRequest,
            PartnerChannelTestKit.FixtureBody("rate-card-currency-refusals.json", "currencyMismatch"), "currency-mismatch");
        body["partnerCurrency"]!.GetValue<string>().Should().Be("PEN");
        body["hostCurrency"]!.GetValue<string>().Should().Be("USD");
        await AssertNothingWrittenAsync(factory);
    }

    [Fact]
    public async Task GenerateCustomerInvoice_ShouldWriteNoInvoice_WhenHostBaseCardMissing()
    {
        await using var factory = new PartnerApiFactory();
        _ = factory.Server;
        await SeedCardAsync(factory, PartnerApiFactory.PartnerTenantId, "USD", 0.20m);

        var response = await GenerateAsync(factory);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertNothingWrittenAsync(factory);
    }

    [Fact]
    public async Task GenerateCustomerInvoice_ShouldReturn400CurrencyInvalid_WhenStoredCardCurrencyIsLegacyText()
    {
        await using var factory = new PartnerApiFactory();
        _ = factory.Server;
        await SeedCardAsync(factory, PartnerApiFactory.HostTenantId, "USD", 0.05m);
        await SeedCardAsync(factory, PartnerApiFactory.PartnerTenantId, "banana", 0.20m);

        var response = await GenerateAsync(factory);

        var body = await PartnerChannelTestKit.AssertRefusalAsync(response, HttpStatusCode.BadRequest, InvalidBody, "currency-invalid");
        body["currency"]!.GetValue<string>().Should().Be("banana");
        await AssertNothingWrittenAsync(factory);
    }

    [Fact]
    public async Task GenerateCustomerInvoice_ShouldPriceTheMarginInOneCurrency_WhenBothCardsAgree()
    {
        await using var factory = new PartnerApiFactory();
        _ = factory.Server;
        await SeedCardAsync(factory, PartnerApiFactory.HostTenantId, "USD", 0.05m);
        await SeedCardAsync(factory, PartnerApiFactory.PartnerTenantId, "usd", 0.20m);

        var response = await GenerateAsync(factory);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await factory.RevenueStore.ListAsync(new TenantId(PartnerApiFactory.PartnerTenantId), null, null, CancellationToken.None))
            .Should().ContainSingle("the invoice and its revenue record are written once nothing refuses");
    }
}
