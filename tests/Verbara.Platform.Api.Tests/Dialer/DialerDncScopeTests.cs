using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Compliance;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions (design D2), against Pro's Postgres stores and checker: a
/// Global list the host operator creates on <c>/management/dnc-lists</c> is owned by the host tenant, so Pro's checker
/// blocks its numbers for every tenant's campaigns whose DNC check is on.
/// </summary>
public sealed class DialerDncScopeTests : IClassFixture<DialerTenantScopeLiveDbFixture>
{
    private readonly DialerTenantScopeLiveDbFixture _fixture;

    public DialerDncScopeTests(DialerTenantScopeLiveDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PostgresDncChecker_ShouldBlockAnotherTenantsNumber_WhenAListCreatedOnTheManagementRouteHoldsIt()
    {
        using var host = _fixture.Factory.CreateAdminClient(DialerTenantScopeFactory.HostTenantId);
        const string phone = "573005550000";

        var created = await host.PostAsync("/api/management/dnc-lists",
            JsonContent.Create(new { name = $"platform-{Guid.NewGuid():N}", scope = "Global" }));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
        (await host.PostAsync($"/api/management/dnc-lists/{id}/entries", JsonContent.Create(new { phoneNumber = phone })))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var checker = _fixture.Factory.Services.GetRequiredService<DncCheckerBase>();

        (await checker.IsBlockedAsync(DialerTenantScopeFactory.AcmeTenantId, phone, dncListId: null, checkGlobal: true, CancellationToken.None))
            .Should().BeTrue("a host-owned Global list blocks the number for every tenant's campaigns with the check on");
        (await checker.IsBlockedAsync(DialerTenantScopeFactory.AcmeTenantId, phone, dncListId: null, checkGlobal: false, CancellationToken.None))
            .Should().BeFalse("a campaign with its DNC check off consults no platform list");
    }
}
