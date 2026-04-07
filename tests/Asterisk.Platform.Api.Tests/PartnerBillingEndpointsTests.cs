using System.Net;
using System.Net.Http.Json;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Tests;

public sealed class PartnerBillingEndpointsTests : IClassFixture<PartnerApiFactory>
{
    private readonly PartnerApiFactory _factory;
    private readonly HttpClient _client;

    public PartnerBillingEndpointsTests(PartnerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePartnerClient();
    }

    private static object BuildRateCardRequest(string name, bool isDefault = false) => new
    {
        name,
        currency = "USD",
        effectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        effectiveTo = (DateTimeOffset?)null,
        isDefault,
        rates = new[]
        {
            new
            {
                usageType = "VoiceInbound",
                unitPrice = 0.01m,
                includedQuantity = 0m,
                tiers = (object[]?)null,
            },
        },
    };

    [Fact]
    public async Task ListRateCards_ShouldReturnPartnerCards()
    {
        var response = await _client.GetAsync("/api/v1/partner/rate-cards");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateRateCard_ShouldSucceed()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/partner/rate-cards", BuildRateCardRequest("Test Rate Card"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Test Rate Card");
        body.Should().Contain("USD");
    }

    [Fact]
    public async Task UpdateRateCard_ShouldSucceed()
    {
        // Create first
        var createResp = await _client.PostAsJsonAsync(
            "/api/v1/partner/rate-cards", BuildRateCardRequest("To Update"));
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await createResp.Content.ReadAsStringAsync();
        // Extract rateCardId — the JSON contains "rateCardId":"<guid>"
        var idStart = body.IndexOf("\"rateCardId\":\"", StringComparison.Ordinal) + "\"rateCardId\":\"".Length;
        var idEnd = body.IndexOf('"', idStart);
        var rateCardId = body[idStart..idEnd];

        // Update
        var updateResp = await _client.PutAsJsonAsync(
            $"/api/v1/partner/rate-cards/{rateCardId}", BuildRateCardRequest("Updated Name"));
        updateResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedBody = await updateResp.Content.ReadAsStringAsync();
        updatedBody.Should().Contain("Updated Name");
    }

    [Fact]
    public async Task DeleteRateCard_ShouldSucceed()
    {
        // Create first
        var createResp = await _client.PostAsJsonAsync(
            "/api/v1/partner/rate-cards", BuildRateCardRequest("To Delete"));
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await createResp.Content.ReadAsStringAsync();
        var idStart = body.IndexOf("\"rateCardId\":\"", StringComparison.Ordinal) + "\"rateCardId\":\"".Length;
        var idEnd = body.IndexOf('"', idStart);
        var rateCardId = body[idStart..idEnd];

        // Delete
        var deleteResp = await _client.DeleteAsync($"/api/v1/partner/rate-cards/{rateCardId}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify it's gone
        var listResp = await _client.GetAsync("/api/v1/partner/rate-cards");
        var listBody = await listResp.Content.ReadAsStringAsync();
        listBody.Should().NotContain(rateCardId);
    }

    [Fact]
    public async Task GenerateCustomerInvoice_ShouldCreateRevenueRecord()
    {
        // Seed a rate card for the host tenant (needed for base cost calculation)
        // and for the partner tenant (needed for invoice generation).
        var hostTid = new TenantId(PartnerApiFactory.HostTenantId);
        var partnerTid = new TenantId(PartnerApiFactory.PartnerTenantId);
        var now = DateTimeOffset.UtcNow;

        var hostCard = new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = hostTid,
            Name = "Host Base Rate Card",
            Currency = "USD",
            EffectiveFrom = now.AddDays(-30),
            IsDefault = true,
            Rates = [new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.005m }],
        };
        await _factory.RateCardStore.SaveAsync(hostCard, CancellationToken.None);

        var partnerCard = new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = partnerTid,
            Name = "Partner Rate Card",
            Currency = "USD",
            EffectiveFrom = now.AddDays(-30),
            IsDefault = true,
            Rates = [new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.01m }],
        };
        await _factory.RateCardStore.SaveAsync(partnerCard, CancellationToken.None);

        var customerId = PartnerApiFactory.CustomerTenantId;
        var from = Uri.EscapeDataString(now.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.ToString("O"));

        var response = await _client.PostAsync(
            $"/api/v1/partner/customers/{customerId}/invoices/generate?from={from}&to={to}", null);

        // Invoice generation with no usage records still succeeds (zero invoice)
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invoice");
        body.Should().Contain("revenue");
    }

    [Fact]
    public async Task GetCustomerUsage_ShouldReturnSummary()
    {
        var response = await _client.GetAsync(
            $"/api/v1/partner/customers/{PartnerApiFactory.CustomerTenantId}/usage");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Returns array (may be empty if no usage records seeded)
        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("[");
    }
}
