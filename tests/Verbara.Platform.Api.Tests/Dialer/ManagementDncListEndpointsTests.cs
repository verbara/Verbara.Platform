using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Compliance;
using Verbara.Sdk.Pro.Dialer.Models;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions (design D2): the host operator manages platform-wide DNC lists on
/// <c>/management/dnc-lists</c>, owned by the host tenant; a customer administrator neither reaches the route nor sees
/// the host's lists or entries on <c>/admin/dnc-lists</c>. The in-memory DNC store follows the Pro contract (an entry
/// operation on another tenant's list answers "no such list"); the same rule against Pro's Postgres checker is
/// <see cref="DialerDncScopeTests"/>.
/// </summary>
public sealed class ManagementDncListEndpointsTests : IClassFixture<DialerTenantScopeFactory>
{
    private const string Host = DialerTenantScopeFactory.HostTenantId;
    private const string Acme = DialerTenantScopeFactory.AcmeTenantId;
    private const string Phone = "573001234567";

    private readonly DialerTenantScopeFactory _factory;

    public ManagementDncListEndpointsTests(DialerTenantScopeFactory factory)
    {
        _factory = factory;
    }

    private static async Task<long> CreateHostGlobalListAsync(HttpClient host, string name)
    {
        var response = await host.PostAsync("/api/management/dnc-lists", JsonContent.Create(new { name, scope = "Global" }));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    [Fact]
    public async Task CreateDncList_ShouldReturn201OwnedByTheHostTenant_WhenTheHostOperatorAsksForGlobalScope()
    {
        using var host = _factory.CreateAdminClient(Host);
        var name = $"platform-dnc-{Guid.NewGuid():N}";

        var id = await CreateHostGlobalListAsync(host, name);

        var store = _factory.Services.GetRequiredService<DncListStoreBase>();
        var stored = await store.GetAsync(id, Host, CancellationToken.None);
        stored.Should().NotBeNull("the list is owned by the host tenant");
        stored!.Scope.Should().Be(DncListScope.Global);
        var listed = JsonNode.Parse(await host.GetStringAsync("/api/management/dnc-lists"))!.AsArray();
        listed.Should().Contain(l => l!["name"]!.GetValue<string>() == name);
    }

    [Fact]
    public async Task AddEntry_ShouldReturn201_WhenTheHostOperatorAddsToItsGlobalList()
    {
        using var host = _factory.CreateAdminClient(Host);
        var id = await CreateHostGlobalListAsync(host, $"platform-dnc-{Guid.NewGuid():N}");

        var response = await host.PostAsync($"/api/management/dnc-lists/{id}/entries", JsonContent.Create(new { phoneNumber = Phone }));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var entries = JsonNode.Parse(await host.GetStringAsync($"/api/management/dnc-lists/{id}/entries"))!.AsArray();
        entries.Select(e => e!["phoneNumber"]!.GetValue<string>()).Should().Equal(Phone);
    }

    [Fact]
    public async Task ListDncLists_ShouldReturn403_WhenACustomerAdministratorCallsTheManagementRoute()
    {
        using var acme = _factory.CreateAdminClient(Acme);

        var list = await acme.GetAsync("/api/management/dnc-lists");
        var create = await acme.PostAsync("/api/management/dnc-lists", JsonContent.Create(new { name = "acme-global", scope = "Global" }));

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListDncLists_ShouldNotShowTheHostList_WhenATenantAdministratorListsItsOwn()
    {
        using var host = _factory.CreateAdminClient(Host);
        using var acme = _factory.CreateAdminClient(Acme);
        var name = $"platform-dnc-{Guid.NewGuid():N}";
        await CreateHostGlobalListAsync(host, name);

        var listed = JsonNode.Parse(await acme.GetStringAsync("/api/admin/dnc-lists"))!.AsArray();

        listed.Should().NotContain(l => l!["name"]!.GetValue<string>() == name);
    }

    [Fact]
    public async Task ListEntries_ShouldReturn404_WhenATenantAdministratorNamesTheHostList()
    {
        using var host = _factory.CreateAdminClient(Host);
        using var acme = _factory.CreateAdminClient(Acme);
        var id = await CreateHostGlobalListAsync(host, $"platform-dnc-{Guid.NewGuid():N}");
        (await host.PostAsync($"/api/management/dnc-lists/{id}/entries", JsonContent.Create(new { phoneNumber = Phone })))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var entries = await acme.GetAsync($"/api/admin/dnc-lists/{id}/entries");
        var add = await acme.PostAsync($"/api/admin/dnc-lists/{id}/entries", JsonContent.Create(new { phoneNumber = "573009999999" }));

        entries.StatusCode.Should().Be(HttpStatusCode.NotFound);
        add.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // round-6 audit F3 + P3: before /api/setup there is no host tenant. An ordinary administrator credential is refused
    // by the host-only policy before any handler runs (403); a management key, which that policy admits without the
    // host lookup, reaches the route, which answers 503 "Platform not initialized" like every sibling /management
    // handler, and creates nothing.
    [Fact]
    public async Task CreateDncList_ShouldReturn503_WhenNoHostTenantExists()
    {
        await using var factory = new DialerTenantScopeFactory(dialerConnectionString: null, seedHostTenant: false);
        using var management = factory.CreateManagementKeyClient();

        var response = await management.PostAsync("/api/management/dnc-lists", JsonContent.Create(new { name = "too-early", scope = "Global" }));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
        var store = factory.Services.GetRequiredService<DncListStoreBase>();
        (await store.ListAsync(DialerTenantScopeFactory.HostTenantId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateDncList_ShouldReturn403_WhenNoHostTenantExistsAndTheCallerIsAnAdministrator()
    {
        await using var factory = new DialerTenantScopeFactory(dialerConnectionString: null, seedHostTenant: false);
        using var acme = factory.CreateAdminClient(Acme);

        var response = await acme.PostAsync("/api/management/dnc-lists", JsonContent.Create(new { name = "too-early", scope = "Global" }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
