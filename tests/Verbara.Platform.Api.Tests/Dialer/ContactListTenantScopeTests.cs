using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Models;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// contact-lists-stay-in-their-tenant: the campaign contact-list routes and the disposition-code listing, create,
/// update and delete routes act only on a campaign, list or code of the caller's tenant, through the HTTP pipeline with two Customer
/// tenants (<c>acme</c>, <c>globex</c>) authenticated by real JWTs (<see cref="DialerTenantScopeFactory"/>). The stores
/// are the in-memory ones; a list or code whose own tenant differs from its campaign's tenant is reached through the
/// list routes only against Pro's Postgres stores, in the live-DB suite.
/// </summary>
public sealed class ContactListTenantScopeTests : IClassFixture<DialerTenantScopeFactory>
{
    private const string CampaignsPath = "/api/admin/campaigns";
    private const string Acme = DialerTenantScopeFactory.AcmeTenantId;
    private const string Globex = DialerTenantScopeFactory.GlobexTenantId;

    private readonly DialerTenantScopeFactory _factory;

    public ContactListTenantScopeTests(DialerTenantScopeFactory factory)
    {
        _factory = factory;
    }

    /// <summary>The three routes that name a list: delete, import into it and list its contacts.</summary>
    public static TheoryData<string> ListRoutes => new() { "delete", "import", "contacts" };

    /// <summary>Each list route, through the list's own campaign and through a campaign of the other tenant.</summary>
    public static TheoryData<string, bool> ListRoutesByCampaign => new()
    {
        { "delete", false }, { "import", false }, { "contacts", false },
        { "delete", true }, { "import", true }, { "contacts", true },
    };

    /// <summary>A disposition-code delete through the code's own campaign and through a campaign of the other tenant.</summary>
    public static TheoryData<bool> ThroughOwnCampaign => new() { false, true };

    // ─── Contact lists ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateContactList_ShouldReturn404AndCreateNoList_WhenTheCampaignBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);

        using var response = await globex.PostAsync($"{CampaignsPath}/{campaignId}/contact-lists",
            JsonContent.Create(new { name = "not mine" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so it is no campaign of globex's", campaignId);
        var store = _factory.Services.GetRequiredService<InMemoryContactListStore>();
        (await store.ListContactListsAsync(Acme, campaignId, CancellationToken.None)).Should().BeEmpty();
        (await store.ListContactListsAsync(Globex, campaignId, CancellationToken.None)).Should().BeEmpty(
            "no list of globex is written on acme's campaign");
    }

    [Fact]
    public async Task ListContactLists_ShouldReturn404AndCarryNoList_WhenTheCampaignBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var listName = $"acme-list-{Guid.NewGuid():N}";
        await CreateContactListAsync(acme, campaignId, listName);

        using var response = await globex.GetAsync($"{CampaignsPath}/{campaignId}/contact-lists");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so globex has no contact lists of it to show", campaignId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(listName);
    }

    [Theory]
    [MemberData(nameof(ListRoutesByCampaign))]
    public async Task ListRoute_ShouldReturn404AndLeaveTheListUnchanged_WhenTheListBelongsToAnotherTenant(
        string route, bool throughCallerCampaign)
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var listId = await CreateListWithContactAsync(acme, campaignId);
        var routeCampaignId = throughCallerCampaign ? await CreateCampaignAsync(globex) : campaignId;

        using var response = await SendListRouteAsync(globex, route, routeCampaignId, listId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "list {0} belongs to acme, so it is no list of globex's (through campaign {1})", listId, routeCampaignId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(ContactFirstName);
        await AssertListUnchangedAsync(acme, campaignId, listId);
    }

    [Theory]
    [MemberData(nameof(ListRoutes))]
    public async Task ListRoute_ShouldReturn404AndLeaveTheListUnchanged_WhenTheListBelongsToAnotherCampaign(string route)
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var otherCampaignId = await CreateCampaignAsync(acme);
        var listId = await CreateListWithContactAsync(acme, campaignId);

        using var response = await SendListRouteAsync(acme, route, otherCampaignId, listId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "list {0} belongs to campaign {1}, not to campaign {2}", listId, campaignId, otherCampaignId);
        await AssertListUnchangedAsync(acme, campaignId, listId);
    }

    [Fact]
    public async Task ContactListRoutes_ShouldAnswerAsBefore_WhenTheCampaignAndListBelongToTheTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var listName = $"own-list-{Guid.NewGuid():N}";

        using var create = await acme.PostAsync($"{CampaignsPath}/{campaignId}/contact-lists",
            JsonContent.Create(new { name = listName }));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var listId = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();

        using var lists = await acme.GetAsync($"{CampaignsPath}/{campaignId}/contact-lists");
        lists.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await lists.Content.ReadAsStringAsync())!.AsArray()
            .Select(n => n!["id"]!.GetValue<long>()).Should().Contain(listId);

        using var import = await acme.PostAsync($"{CampaignsPath}/{campaignId}/contact-lists/{listId}/import",
            ImportBody(("Ana", "+15551230001"), ("Luis", "+15551230002")));
        import.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await import.Content.ReadAsStringAsync())!["imported"]!.GetValue<int>().Should().Be(2);

        using var contacts = await acme.GetAsync($"{CampaignsPath}/{campaignId}/contact-lists/{listId}/contacts");
        contacts.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = JsonNode.Parse(await contacts.Content.ReadAsStringAsync())!;
        page["items"]!.AsArray().Select(n => n!["firstName"]!.GetValue<string>()).Should().BeEquivalentTo(["Ana", "Luis"]);

        using var delete = await acme.DeleteAsync($"{CampaignsPath}/{campaignId}/contact-lists/{listId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── Disposition codes ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDisposition_ShouldReturn404AndCreateNoCode_WhenTheCampaignBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);

        using var response = await globex.PostAsync($"{CampaignsPath}/{campaignId}/dispositions", DispositionBody("NOT-MINE"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so it is no campaign of globex's", campaignId);
        var store = _factory.Services.GetRequiredService<InMemoryDispositionCodeStore>();
        (await store.ListByCampaignAsync(Acme, campaignId, CancellationToken.None)).Should().BeEmpty();
        (await store.ListByCampaignAsync(Globex, campaignId, CancellationToken.None)).Should().BeEmpty(
            "no code of globex is written on acme's campaign");
    }

    [Theory]
    [MemberData(nameof(ThroughOwnCampaign))]
    public async Task DeleteDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeBelongsToAnotherTenant(
        bool throughCallerCampaign)
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var codeId = await CreateDispositionAsync(acme, campaignId);
        var routeCampaignId = throughCallerCampaign ? await CreateCampaignAsync(globex) : campaignId;

        using var response = await globex.DeleteAsync($"{CampaignsPath}/{routeCampaignId}/dispositions/{codeId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "code {0} belongs to acme, so it is no code of globex's (through campaign {1})", codeId, routeCampaignId);
        (await DispositionIdsAsync(acme, campaignId)).Should().Contain(codeId);
    }

    [Fact]
    public async Task DeleteDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeBelongsToAnotherCampaign()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var otherCampaignId = await CreateCampaignAsync(acme);
        var codeId = await CreateDispositionAsync(acme, campaignId);

        using var response = await acme.DeleteAsync($"{CampaignsPath}/{otherCampaignId}/dispositions/{codeId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "code {0} belongs to campaign {1}, not to campaign {2}", codeId, campaignId, otherCampaignId);
        (await DispositionIdsAsync(acme, campaignId)).Should().Contain(codeId);
    }

    /// <summary>Neither tenant removes the code: not the code's tenant (globex), not the campaign's tenant (acme).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeTenantDiffersFromItsCampaignTenant(
        bool callerIsCampaignTenant)
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var store = _factory.Services.GetRequiredService<InMemoryDispositionCodeStore>();
        var seeded = await store.CreateAsync(Globex, new DispositionCode
        {
            TenantId = Globex,
            CampaignId = campaignId,
            Code = "MISMATCHED",
            Label = "Mismatched",
            IsActive = true,
        }, CancellationToken.None);

        var codeId = seeded.Id;
        var caller = callerIsCampaignTenant ? acme : globex;

        using var response = await caller.DeleteAsync($"{CampaignsPath}/{campaignId}/dispositions/{codeId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "code {0} is globex's on acme's campaign {1}, so it is a code of neither tenant (caller {2})",
            codeId, campaignId, callerIsCampaignTenant ? Acme : Globex);
        (await store.ListByCampaignAsync(Globex, campaignId, CancellationToken.None))
            .Select(d => d.Id).Should().Contain(seeded.Id, "the code is still stored");
    }

    /// <summary>Neither tenant updates the code: not the code's tenant (globex), not the campaign's tenant (acme).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeTenantDiffersFromItsCampaignTenant(
        bool callerIsCampaignTenant)
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var store = _factory.Services.GetRequiredService<InMemoryDispositionCodeStore>();
        var seeded = await store.CreateAsync(Globex, new DispositionCode
        {
            TenantId = Globex,
            CampaignId = campaignId,
            Code = "MISMATCHED",
            Label = "Mismatched",
            IsActive = true,
        }, CancellationToken.None);
        var caller = callerIsCampaignTenant ? acme : globex;

        using var response = await caller.PutAsync($"{CampaignsPath}/{campaignId}/dispositions/{seeded.Id}",
            RenameBody);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "code {0} is globex's on acme's campaign {1}, so it is a code of neither tenant (caller {2})",
            seeded.Id, campaignId, callerIsCampaignTenant ? Acme : Globex);
        var stored = (await store.ListByCampaignAsync(Globex, campaignId, CancellationToken.None))
            .Single(d => d.Id == seeded.Id);
        (stored.TenantId, stored.CampaignId, stored.Code, stored.Label)
            .Should().Be((Globex, campaignId, "MISMATCHED", "Mismatched"), "the code is unchanged");
    }

    [Theory]
    [MemberData(nameof(ThroughOwnCampaign))]
    public async Task UpdateDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeBelongsToAnotherTenant(
        bool throughCallerCampaign)
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var codeId = await CreateDispositionAsync(acme, campaignId);
        var label = await DispositionLabelAsync(acme, campaignId, codeId);
        var routeCampaignId = throughCallerCampaign ? await CreateCampaignAsync(globex) : campaignId;

        using var response = await globex.PutAsync($"{CampaignsPath}/{routeCampaignId}/dispositions/{codeId}", RenameBody);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "code {0} belongs to acme, so it is no code of globex's (through campaign {1})", codeId, routeCampaignId);
        (await DispositionLabelAsync(acme, campaignId, codeId)).Should().Be(label, "the code keeps its label");
    }

    [Fact]
    public async Task UpdateDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeBelongsToAnotherCampaign()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var otherCampaignId = await CreateCampaignAsync(acme);
        var codeId = await CreateDispositionAsync(acme, campaignId);
        var label = await DispositionLabelAsync(acme, campaignId, codeId);

        using var response = await acme.PutAsync($"{CampaignsPath}/{otherCampaignId}/dispositions/{codeId}", RenameBody);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "code {0} belongs to campaign {1}, not to campaign {2}", codeId, campaignId, otherCampaignId);
        (await DispositionLabelAsync(acme, campaignId, codeId)).Should().Be(label, "the code keeps its label");
    }

    [Fact]
    public async Task ListDispositions_ShouldReturn404AndCarryNoCode_WhenTheCodeTenantDiffersFromItsCampaignTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var seeded = await SeedMismatchedDispositionAsync(campaignId);

        using var response = await globex.GetAsync($"{CampaignsPath}/{campaignId}/dispositions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so globex has no codes of it to show (code {1} is globex's)",
            campaignId, seeded.Id);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Mismatched");
    }

    [Fact]
    public async Task ListDispositions_ShouldListOnlyItsOwnCode_WhenTheCampaignCarriesACodeOfAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var seeded = await SeedMismatchedDispositionAsync(campaignId);
        var codeId = await CreateDispositionAsync(acme, campaignId);

        var ids = await DispositionIdsAsync(acme, campaignId);

        ids.Should().Equal([codeId], "acme sees its own code of campaign {0} and not globex's code {1}",
            campaignId, seeded.Id);
    }

    [Fact]
    public async Task ListDispositions_ShouldReturn404AndCarryNoCode_WhenTheCampaignBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var codeId = await CreateDispositionAsync(acme, campaignId);
        var label = await DispositionLabelAsync(acme, campaignId, codeId);

        using var response = await globex.GetAsync($"{CampaignsPath}/{campaignId}/dispositions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so it is no campaign of globex's", campaignId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(label);
    }

    [Fact]
    public async Task ListDispositions_ShouldReturn404_WhenTheCampaignDoesNotExist()
    {
        using var acme = _factory.CreateAdminClient(Acme);

        using var response = await acme.GetAsync($"{CampaignsPath}/{UnknownCampaignId}/dispositions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "no store holds campaign {0}", UnknownCampaignId);
    }

    [Fact]
    public async Task ListDispositions_ShouldNotListTheCode_WhenTheCodeBelongsToAnotherCampaign()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var otherCampaignId = await CreateCampaignAsync(acme);
        var codeId = await CreateDispositionAsync(acme, campaignId);

        var ids = await DispositionIdsAsync(acme, otherCampaignId);

        ids.Should().NotContain(codeId, "code {0} belongs to campaign {1}, not to campaign {2}",
            codeId, campaignId, otherCampaignId);
    }

    [Fact]
    public async Task DispositionRoutes_ShouldAnswerAsBefore_WhenTheCampaignAndCodeBelongToTheTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);

        using var create = await acme.PostAsync($"{CampaignsPath}/{campaignId}/dispositions", DispositionBody("SALE"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var codeId = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();

        using var list = await acme.GetAsync($"{CampaignsPath}/{campaignId}/dispositions");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray()
            .Select(n => n!["id"]!.GetValue<long>()).Should().Equal([codeId]);

        using var update = await acme.PutAsync($"{CampaignsPath}/{campaignId}/dispositions/{codeId}", RenameBody);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DispositionLabelAsync(acme, campaignId, codeId)).Should().Be(RenamedLabel);

        using var delete = await acme.DeleteAsync($"{CampaignsPath}/{campaignId}/dispositions/{codeId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DispositionIdsAsync(acme, campaignId)).Should().NotContain(codeId);
    }

    // ─── Helpers (shared with the live-DB suite) ─────────────────────────────────

    internal const string ContactFirstName = "Original";

    /// <summary>A campaign id that no store holds.</summary>
    internal const long UnknownCampaignId = 9_000_000_000_000L;

    /// <summary>A code of globex, <c>MISMATCHED</c> / <c>Mismatched</c>, stored on <paramref name="campaignId"/>.</summary>
    private async Task<DispositionCode> SeedMismatchedDispositionAsync(long campaignId) =>
        await _factory.Services.GetRequiredService<InMemoryDispositionCodeStore>().CreateAsync(Globex, new DispositionCode
        {
            TenantId = Globex,
            CampaignId = campaignId,
            Code = "MISMATCHED",
            Label = "Mismatched",
            IsActive = true,
        }, CancellationToken.None);

    internal static async Task<HttpResponseMessage> SendListRouteAsync(
        HttpClient client, string route, long campaignId, long listId) =>
        route switch
        {
            "delete" => await client.DeleteAsync($"{CampaignsPath}/{campaignId}/contact-lists/{listId}"),
            "import" => await client.PostAsync($"{CampaignsPath}/{campaignId}/contact-lists/{listId}/import",
                ImportBody(("Intruder", "+15559990001"))),
            "contacts" => await client.GetAsync($"{CampaignsPath}/{campaignId}/contact-lists/{listId}/contacts"),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };

    internal static async Task<long> CreateCampaignAsync(HttpClient client)
    {
        using var response = await client.PostAsync(CampaignsPath, JsonContent.Create(new
        {
            name = $"campaign-{Guid.NewGuid():N}",
            mode = "progressive",
            targetQueueName = "default",
            maxConcurrentCalls = 5,
            timezone = "UTC",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = false,
            maxAttemptsPerContact = 3,
            retryIntervalMinutes = 60,
            timeBetweenAttemptsMinutes = 5,
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    internal static async Task<long> CreateContactListAsync(HttpClient client, long campaignId, string name)
    {
        using var response = await client.PostAsync($"{CampaignsPath}/{campaignId}/contact-lists",
            JsonContent.Create(new { name }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    /// <summary>A list of <paramref name="campaignId"/> holding exactly one contact, <see cref="ContactFirstName"/>.</summary>
    internal static async Task<long> CreateListWithContactAsync(HttpClient client, long campaignId)
    {
        var listId = await CreateContactListAsync(client, campaignId, $"list-{Guid.NewGuid():N}");
        using var response = await client.PostAsync($"{CampaignsPath}/{campaignId}/contact-lists/{listId}/import",
            ImportBody((ContactFirstName, "+15551110000")));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return listId;
    }

    /// <summary>The list is still among its campaign's lists and holds exactly its one contact.</summary>
    internal static async Task AssertListUnchangedAsync(HttpClient owner, long campaignId, long listId)
    {
        var lists = JsonNode.Parse(await owner.GetStringAsync($"{CampaignsPath}/{campaignId}/contact-lists"))!.AsArray();
        lists.Select(n => n!["id"]!.GetValue<long>()).Should().Contain(listId, "the list still exists");
        var page = JsonNode.Parse(
            await owner.GetStringAsync($"{CampaignsPath}/{campaignId}/contact-lists/{listId}/contacts"))!;
        page["items"]!.AsArray().Select(n => n!["firstName"]!.GetValue<string>())
            .Should().Equal([ContactFirstName], "the list still holds exactly its one contact");
    }

    internal static async Task<long> CreateDispositionAsync(HttpClient client, long campaignId)
    {
        using var response = await client.PostAsync($"{CampaignsPath}/{campaignId}/dispositions",
            DispositionBody($"C{Guid.NewGuid():N}"[..12]));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    internal static async Task<IReadOnlyList<long>> DispositionIdsAsync(HttpClient client, long campaignId)
    {
        var codes = JsonNode.Parse(await client.GetStringAsync($"{CampaignsPath}/{campaignId}/dispositions"))!.AsArray();
        return codes.Select(n => n!["id"]!.GetValue<long>()).ToList();
    }

    internal const string RenamedLabel = "Renamed";

    /// <summary>A disposition-code update that renames the code to <see cref="RenamedLabel"/>.</summary>
    internal static JsonContent RenameBody => JsonContent.Create(new { label = RenamedLabel });

    /// <summary>The label of <paramref name="codeId"/> in its campaign's listing, as <paramref name="client"/> sees it.</summary>
    internal static async Task<string> DispositionLabelAsync(HttpClient client, long campaignId, long codeId)
    {
        var codes = JsonNode.Parse(await client.GetStringAsync($"{CampaignsPath}/{campaignId}/dispositions"))!.AsArray();
        return codes.Single(n => n!["id"]!.GetValue<long>() == codeId)!["label"]!.GetValue<string>();
    }

    private static JsonContent DispositionBody(string code) => JsonContent.Create(new
    {
        code,
        label = code,
        category = "failure",
        isSuccess = false,
        triggerRetry = false,
        triggerCallback = false,
    });

    private static JsonContent ImportBody(params (string FirstName, string Phone)[] contacts) => JsonContent.Create(new
    {
        contacts = contacts.Select(c => new { firstName = c.FirstName, lastName = "Test", phone = c.Phone }).ToArray(),
    });
}
