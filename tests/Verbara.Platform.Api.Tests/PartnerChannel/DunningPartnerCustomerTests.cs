using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.PartnerChannel;

/// <summary>
/// partner-channel-pilot-fixes P1 and D16 (partner-channel-tenancy): the three dunning routes stay host-only
/// (a Partner administrator gets 403, a pin of existing behaviour), and the host's routes refuse a Customer whose
/// parent is a Partner with 409 <c>dunning-partner-customer</c> before any dunning record is read or written.
/// A direct Customer keeps host dunning.
/// </summary>
public sealed class DunningPartnerCustomerTests : IClassFixture<PartnerApiFactory>, IClassFixture<PlatformAdminApiFactory>
{
    private static readonly string[] Routes = ["dunning", "dunning/pause", "dunning/resume"];
    private readonly PartnerApiFactory _partner;
    private readonly PlatformAdminApiFactory _platform;

    public DunningPartnerCustomerTests(PartnerApiFactory partner, PlatformAdminApiFactory platform)
    {
        _partner = partner;
        _platform = platform;
    }

    private static async Task SeedRecordAsync(IServiceProvider services, string tenantId, bool isPaused = false)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDunningStore>().UpsertAsync(new DunningRecord
        {
            DunningId = $"dunning-{tenantId}",
            TenantId = tenantId,
            InvoiceId = $"invoice-{tenantId}",
            CurrentStage = TenantStatus.Warning,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-3),
            IsPaused = isPaused,
            IsActive = true,
        }, CancellationToken.None);
    }

    private static async Task<DunningRecord?> RecordAsync(IServiceProvider services, string tenantId)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDunningStore>().GetActiveAsync(tenantId, CancellationToken.None);
    }

    private static Task<HttpResponseMessage> CallAsync(HttpClient client, string tenantId, string route) =>
        route == "dunning"
            ? client.GetAsync($"/api/v1/management/tenants/{tenantId}/{route}")
            : client.PostAsync($"/api/v1/management/tenants/{tenantId}/{route}", null);

    private async Task<(string Partner, string Customer)> SeedPartnerCustomerAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var partnerId = "dun-p-" + suffix;
        var customerId = "dun-pc-" + suffix;
        _ = _platform.Server;
        await PartnerChannelTestKit.SeedTenantAsync(_platform.Services, partnerId, TenantType.Partner,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);
        await PartnerChannelTestKit.SeedTenantAsync(_platform.Services, customerId, TenantType.Customer, partnerId, TenantPlan.Pro);
        return (partnerId, customerId);
    }

    [Fact]
    public async Task Dunning_ShouldReturn403_WhenCallerIsPartnerAdmin()
    {
        var client = _partner.CreatePartnerClient();
        await SeedRecordAsync(_partner.Services, PartnerApiFactory.CustomerTenantId);

        foreach (var route in Routes)
        {
            var response = await CallAsync(client, PartnerApiFactory.CustomerTenantId, route);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{route} is PlatformAdminOnly, also for the Partner's own Customer");
        }

        var record = await RecordAsync(_partner.Services, PartnerApiFactory.CustomerTenantId);
        record!.IsPaused.Should().BeFalse("no dunning state changes");
        record.CurrentStage.Should().Be(TenantStatus.Warning);
    }

    [Fact]
    public async Task Dunning_ShouldReturn409DunningPartnerCustomer_WhenTenantIsPartnerCustomer()
    {
        var (partnerId, customerId) = await SeedPartnerCustomerAsync();
        var client = _platform.CreatePlatformAdminClient();
        var fixture = PartnerChannelTestKit.FixtureBody("dunning-partner-customer-409.json");

        foreach (var route in Routes)
        {
            var response = await CallAsync(client, customerId, route);
            var body = await PartnerChannelTestKit.AssertRefusalAsync(response, HttpStatusCode.Conflict, fixture, "dunning-partner-customer");
            body["tenantId"]!.GetValue<string>().Should().Be(customerId, route);
            body["partnerTenantId"]!.GetValue<string>().Should().Be(partnerId, route);
        }

        (await RecordAsync(_platform.Services, customerId)).Should().BeNull("nothing is written");
    }

    [Fact]
    public async Task PauseDunning_ShouldReturn409AndLeaveRecord_WhenPartnerCustomerHasPreexistingRecord()
    {
        var (_, customerId) = await SeedPartnerCustomerAsync();
        await SeedRecordAsync(_platform.Services, customerId);

        var response = await CallAsync(_platform.CreatePlatformAdminClient(), customerId, "dunning/pause");

        await PartnerChannelTestKit.AssertRefusalAsync(response, HttpStatusCode.Conflict,
            PartnerChannelTestKit.FixtureBody("dunning-partner-customer-409.json"), "dunning-partner-customer");
        var record = await RecordAsync(_platform.Services, customerId);
        record!.IsPaused.Should().BeFalse("the record opened before this change is unchanged");
        record.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task PauseDunning_ShouldPause_WhenTenantIsDirectCustomer()
    {
        var customerId = "dun-dc-" + Guid.NewGuid().ToString("N")[..8];
        _ = _platform.Server;
        await PartnerChannelTestKit.SeedTenantAsync(_platform.Services, customerId, TenantType.Customer,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);
        await SeedRecordAsync(_platform.Services, customerId);

        var response = await CallAsync(_platform.CreatePlatformAdminClient(), customerId, "dunning/pause");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RecordAsync(_platform.Services, customerId))!.IsPaused.Should().BeTrue("a direct Customer keeps host dunning");
    }
}
