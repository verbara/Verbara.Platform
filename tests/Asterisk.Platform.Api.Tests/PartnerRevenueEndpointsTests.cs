using System.Net;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Tests;

public sealed class PartnerRevenueEndpointsTests : IClassFixture<PartnerApiFactory>
{
    private readonly PartnerApiFactory _factory;
    private readonly HttpClient _client;

    public PartnerRevenueEndpointsTests(PartnerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePartnerClient();
    }

    private static PartnerRevenueRecord BuildRecord(
        DateTimeOffset periodStart, DateTimeOffset periodEnd,
        decimal gross = 100m, decimal cost = 60m) =>
        new()
        {
            RevenueId = EntityId.New(),
            PartnerTenantId = new TenantId(PartnerApiFactory.PartnerTenantId),
            CustomerTenantId = new TenantId(PartnerApiFactory.CustomerTenantId),
            InvoiceId = EntityId.New(),
            GrossAmount = gross,
            PlatformCost = cost,
            PartnerMargin = gross - cost,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task GetRevenueSummary_ShouldAggregateMargins()
    {
        var now = DateTimeOffset.UtcNow;
        var record = BuildRecord(now.AddDays(-30), now, gross: 200m, cost: 120m);
        await _factory.RevenueStore.UpsertAsync(record, CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/partner/revenue/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        // The summary should contain totalGross, totalMargin fields
        body.Should().Contain("totalGross");
        body.Should().Contain("totalMargin");
    }

    [Fact]
    public async Task GetRevenueSummary_ShouldReturnZero_WhenNoRecords()
    {
        // Use a fresh factory instance via a scope-limited query with a future date range
        var farFuture = DateTimeOffset.UtcNow.AddYears(10);
        var from = Uri.EscapeDataString(farFuture.ToString("O"));
        var to = Uri.EscapeDataString(farFuture.AddDays(1).ToString("O"));

        var response = await _client.GetAsync($"/api/v1/partner/revenue/?from={from}&until={to}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"totalGross\":0");
        body.Should().Contain("\"invoiceCount\":0");
    }

    [Fact]
    public async Task GetRevenueDetails_ShouldReturnAllRecords()
    {
        var now = DateTimeOffset.UtcNow;
        var record1 = BuildRecord(now.AddDays(-60), now.AddDays(-30), gross: 150m, cost: 90m);
        var record2 = BuildRecord(now.AddDays(-30), now, gross: 250m, cost: 140m);
        await _factory.RevenueStore.UpsertAsync(record1, CancellationToken.None);
        await _factory.RevenueStore.UpsertAsync(record2, CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/partner/revenue/details");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("revenueId");
        body.Should().Contain("grossAmount");
        body.Should().Contain("partnerMargin");
    }

    [Fact]
    public async Task GetRevenueDetails_ShouldFilterByDateRange()
    {
        var now = DateTimeOffset.UtcNow;
        // Seed a record in the past (outside filter window)
        var oldRecord = BuildRecord(now.AddDays(-365), now.AddDays(-300), gross: 999m, cost: 500m);
        await _factory.RevenueStore.UpsertAsync(oldRecord, CancellationToken.None);

        // Query only within a narrow future window (no records)
        var filterFrom = Uri.EscapeDataString(now.AddYears(5).ToString("O"));
        var filterUntil = Uri.EscapeDataString(now.AddYears(6).ToString("O"));
        var response = await _client.GetAsync(
            $"/api/v1/partner/revenue/details?from={filterFrom}&until={filterUntil}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("[]");
    }
}
