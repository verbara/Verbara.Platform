using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Npgsql;
using static Verbara.Platform.Api.Tests.Dialer.ContactListTenantScopeTests;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// contact-lists-stay-in-their-tenant, against Pro's Postgres stores: the real Api <c>Program</c> with the dialer
/// registered over a Testcontainers Postgres (<see cref="DialerTenantScopeFactory"/> in live-DB mode keeps Pro's
/// contact-list and disposition-code stores). The campaign contact-list routes, the disposition-code listing,
/// create, update and delete routes and the scheduled-callback listing and create routes answer 404 for a campaign, list or code outside the caller's tenant or campaign and change nothing,
/// and a list or code whose own tenant differs from its campaign's tenant is reached by neither tenant.
/// </summary>
public sealed class ContactListTenantScopeLiveDbTests : IClassFixture<DialerTenantScopeLiveDbFixture>
{
    private const string CampaignsPath = "/api/admin/campaigns";
    private const string Acme = DialerTenantScopeFactory.AcmeTenantId;
    private const string Globex = DialerTenantScopeFactory.GlobexTenantId;

    private readonly DialerTenantScopeLiveDbFixture _fixture;

    public ContactListTenantScopeLiveDbTests(DialerTenantScopeLiveDbFixture fixture)
    {
        _fixture = fixture;
    }

    private DialerTenantScopeFactory Factory => _fixture.Factory;

    /// <summary>Each list route, called by the list's own tenant and by the campaign's tenant.</summary>
    public static TheoryData<string, string> ListRoutesByCaller => new()
    {
        { "delete", Globex }, { "import", Globex }, { "contacts", Globex },
        { "delete", Acme }, { "import", Acme }, { "contacts", Acme },
    };

    // ─── Contact lists ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateContactList_ShouldReturn404AndCreateNoList_WhenTheCampaignBelongsToAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);

        using var response = await globex.PostAsync($"{CampaignsPath}/{campaignId}/contact-lists",
            JsonContent.Create(new { name = "not mine" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so it is no campaign of globex's", campaignId);
        (await CountAsync("SELECT COUNT(*) FROM contact_lists WHERE campaign_id = @Id", campaignId))
            .Should().Be(0, "no list is written on acme's campaign");
    }

    [Fact]
    public async Task ListContactLists_ShouldReturn404AndCarryNoList_WhenTheCampaignBelongsToAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var listName = $"acme-list-{Guid.NewGuid():N}";
        await CreateContactListAsync(acme, campaignId, listName);

        using var response = await globex.GetAsync($"{CampaignsPath}/{campaignId}/contact-lists");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so globex has no contact lists of it to show", campaignId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(listName);
    }

    [Theory]
    [MemberData(nameof(ListRoutesByCampaign), MemberType = typeof(ContactListTenantScopeTests))]
    public async Task ListRoute_ShouldReturn404AndLeaveTheListUnchanged_WhenTheListBelongsToAnotherTenant(
        string route, bool throughCallerCampaign)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
    [MemberData(nameof(ListRoutes), MemberType = typeof(ContactListTenantScopeTests))]
    public async Task ListRoute_ShouldReturn404AndLeaveTheListUnchanged_WhenTheListBelongsToAnotherCampaign(string route)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var otherCampaignId = await CreateCampaignAsync(acme);
        var listId = await CreateListWithContactAsync(acme, campaignId);

        using var response = await SendListRouteAsync(acme, route, otherCampaignId, listId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "list {0} belongs to campaign {1}, not to campaign {2}", listId, campaignId, otherCampaignId);
        await AssertListUnchangedAsync(acme, campaignId, listId);
    }

    [Theory]
    [MemberData(nameof(ListRoutesByCaller))]
    public async Task ListRoute_ShouldReturn404AndLeaveTheListUnchanged_WhenTheListTenantDiffersFromItsCampaignTenant(
        string route, string caller)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var listId = await SeedListAsync(Globex, campaignId);
        using var client = Factory.CreateAdminClient(caller);

        using var response = await SendListRouteAsync(client, route, campaignId, listId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "list {0} is globex's but campaign {1} is acme's, so neither tenant reaches it (caller {2})",
            listId, campaignId, caller);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(ContactFirstName);
        (await CountAsync("SELECT COUNT(*) FROM contact_lists WHERE id = @Id", listId)).Should().Be(1, "the list still exists");
        (await ContactFirstNamesAsync(listId)).Should().Equal([ContactFirstName], "the list still holds exactly its contact");
    }

    [Fact]
    public async Task ContactListRoutes_ShouldAnswerAsBefore_WhenTheCampaignAndListBelongToTheTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var listId = await CreateListWithContactAsync(acme, campaignId);

        await AssertListUnchangedAsync(acme, campaignId, listId);
        using var delete = await acme.DeleteAsync($"{CampaignsPath}/{campaignId}/contact-lists/{listId}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CountAsync("SELECT COUNT(*) FROM contact_lists WHERE id = @Id", listId)).Should().Be(0);
    }

    // ─── Disposition codes ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDisposition_ShouldReturn404AndCreateNoCode_WhenTheCampaignBelongsToAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);

        using var response = await globex.PostAsync($"{CampaignsPath}/{campaignId}/dispositions", JsonContent.Create(new
        {
            code = "NOT-MINE",
            label = "Not mine",
            category = "failure",
            isSuccess = false,
            triggerRetry = false,
            triggerCallback = false,
        }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so it is no campaign of globex's", campaignId);
        (await CountAsync("SELECT COUNT(*) FROM disposition_codes WHERE campaign_id = @Id", campaignId))
            .Should().Be(0, "no code is written on acme's campaign");
    }

    [Theory]
    [MemberData(nameof(ThroughOwnCampaign), MemberType = typeof(ContactListTenantScopeTests))]
    public async Task DeleteDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeBelongsToAnotherTenant(
        bool throughCallerCampaign)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var codeId = await CreateDispositionAsync(acme, campaignId);
        var routeCampaignId = throughCallerCampaign ? await CreateCampaignAsync(globex) : campaignId;

        using var response = await globex.DeleteAsync($"{CampaignsPath}/{routeCampaignId}/dispositions/{codeId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "code {0} belongs to acme, so it is no code of globex's (through campaign {1})", codeId, routeCampaignId);
        (await DispositionIdsAsync(acme, campaignId)).Should().Contain(codeId);
    }

    /// <summary>Neither tenant removes the code: not the code's tenant (globex), not the campaign's tenant (acme).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeTenantDiffersFromItsCampaignTenant(
        bool callerIsCampaignTenant)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var codeId = await SeedDispositionAsync(Globex, campaignId);
        var caller = callerIsCampaignTenant ? acme : globex;

        using var response = await caller.DeleteAsync($"{CampaignsPath}/{campaignId}/dispositions/{codeId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "code {0} is globex's on acme's campaign {1}, so it is a code of neither tenant (caller {2})",
            codeId, campaignId, callerIsCampaignTenant ? Acme : Globex);
        (await DispositionRowAsync(codeId)).Should().Be((Globex, campaignId, "MISMATCHED", "Mismatched"),
            "the row is unchanged");
    }

    /// <summary>Neither tenant updates the code: not the code's tenant (globex), not the campaign's tenant (acme).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeTenantDiffersFromItsCampaignTenant(
        bool callerIsCampaignTenant)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var codeId = await SeedDispositionAsync(Globex, campaignId);
        var caller = callerIsCampaignTenant ? acme : globex;

        using var response = await caller.PutAsync($"{CampaignsPath}/{campaignId}/dispositions/{codeId}", RenameBody);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "code {0} is globex's on acme's campaign {1}, so it is a code of neither tenant (caller {2})",
            codeId, campaignId, callerIsCampaignTenant ? Acme : Globex);
        (await DispositionRowAsync(codeId)).Should().Be((Globex, campaignId, "MISMATCHED", "Mismatched"),
            "the row is unchanged");
    }

    [Theory]
    [MemberData(nameof(ThroughOwnCampaign), MemberType = typeof(ContactListTenantScopeTests))]
    public async Task UpdateDisposition_ShouldReturn404AndKeepTheCode_WhenTheCodeBelongsToAnotherTenant(
        bool throughCallerCampaign)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
        using var acme = Factory.CreateAdminClient(Acme);
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
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var codeId = await SeedDispositionAsync(Globex, campaignId);

        using var response = await globex.GetAsync($"{CampaignsPath}/{campaignId}/dispositions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so globex has no codes of it to show (code {1} is globex's)",
            campaignId, codeId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Mismatched");
        (await DispositionRowAsync(codeId)).Should().Be((Globex, campaignId, "MISMATCHED", "Mismatched"),
            "the row is unchanged");
    }

    [Fact]
    public async Task ListDispositions_ShouldListOnlyItsOwnCode_WhenTheCampaignCarriesACodeOfAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var seededId = await SeedDispositionAsync(Globex, campaignId);
        var codeId = await CreateDispositionAsync(acme, campaignId);

        var ids = await DispositionIdsAsync(acme, campaignId);

        ids.Should().Equal([codeId], "acme sees its own code of campaign {0} and not globex's code {1}",
            campaignId, seededId);
        (await DispositionRowAsync(seededId)).Should().Be((Globex, campaignId, "MISMATCHED", "Mismatched"),
            "the row is unchanged");
    }

    [Fact]
    public async Task ListDispositions_ShouldReturn404AndCarryNoCode_WhenTheCampaignBelongsToAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var codeId = await CreateDispositionAsync(acme, campaignId);
        var label = await DispositionLabelAsync(acme, campaignId, codeId);

        using var response = await globex.GetAsync($"{CampaignsPath}/{campaignId}/dispositions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so it is no campaign of globex's", campaignId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(label);
        (await DispositionLabelAsync(acme, campaignId, codeId)).Should().Be(label, "the code keeps its label");
    }

    [Fact]
    public async Task ListDispositions_ShouldReturn404_WhenTheCampaignDoesNotExist()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        (await CountAsync("SELECT COUNT(*) FROM campaigns WHERE id = @Id", UnknownCampaignId))
            .Should().Be(0, "no campaign row has id {0}", UnknownCampaignId);

        using var response = await acme.GetAsync($"{CampaignsPath}/{UnknownCampaignId}/dispositions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "no store holds campaign {0}", UnknownCampaignId);
    }

    [Fact]
    public async Task ListDispositions_ShouldNotListTheCode_WhenTheCodeBelongsToAnotherCampaign()
    {
        using var acme = Factory.CreateAdminClient(Acme);
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
        using var acme = Factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var codeId = await CreateDispositionAsync(acme, campaignId);

        using var list = await acme.GetAsync($"{CampaignsPath}/{campaignId}/dispositions");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DispositionIdsAsync(acme, campaignId)).Should().Equal([codeId]);

        using var update = await acme.PutAsync($"{CampaignsPath}/{campaignId}/dispositions/{codeId}", RenameBody);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DispositionLabelAsync(acme, campaignId, codeId)).Should().Be(RenamedLabel);

        using var delete = await acme.DeleteAsync($"{CampaignsPath}/{campaignId}/dispositions/{codeId}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DispositionIdsAsync(acme, campaignId)).Should().NotContain(codeId);
    }

    // ─── Scheduled callbacks ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListCallbacks_ShouldReturn404AndCarryNoCallback_WhenTheCallbackTenantDiffersFromItsCampaignTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var contactId = NextContactId();
        await SeedCallbackAsync(Globex, campaignId, contactId);

        using var response = await globex.GetAsync($"{CampaignsPath}/{campaignId}/callbacks");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so globex has no callbacks of it to show (contact {1} is globex's)",
            campaignId, contactId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(contactId.ToString(CultureInfo.InvariantCulture));
        (await CountCallbacksAsync(contactId)).Should().Be(1, "the seeded row is still there");
    }

    [Fact]
    public async Task ListCallbacks_ShouldListOnlyItsOwnCallback_WhenTheCampaignCarriesACallbackOfAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var foreignContactId = NextContactId();
        var ownContactId = NextContactId();
        await SeedCallbackAsync(Globex, campaignId, foreignContactId);
        await SeedCallbackAsync(Acme, campaignId, ownContactId);

        var contactIds = await CallbackContactIdsAsync(acme, campaignId);

        contactIds.Should().Equal([ownContactId], "acme sees its own callback of campaign {0} and not globex's of contact {1}",
            campaignId, foreignContactId);
        (await CountCallbacksAsync(foreignContactId)).Should().Be(1, "the seeded row is still there");
    }

    [Fact]
    public async Task ListCallbacks_ShouldReturn404AndCarryNoCallback_WhenTheCampaignBelongsToAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var contactId = NextContactId();
        await SeedCallbackAsync(Acme, campaignId, contactId);

        using var response = await globex.GetAsync($"{CampaignsPath}/{campaignId}/callbacks");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so it is no campaign of globex's", campaignId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(contactId.ToString(CultureInfo.InvariantCulture));
        (await CallbackContactIdsAsync(acme, campaignId)).Should().Equal([contactId], "acme still lists its callback");
    }

    [Fact]
    public async Task ListCallbacks_ShouldNotListTheCallback_WhenTheCallbackBelongsToAnotherCampaign()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var otherCampaignId = await CreateCampaignAsync(acme);
        var contactId = NextContactId();
        await SeedCallbackAsync(Acme, campaignId, contactId);

        var contactIds = await CallbackContactIdsAsync(acme, otherCampaignId);

        contactIds.Should().NotContain(contactId, "the callback of contact {0} belongs to campaign {1}, not to campaign {2}",
            contactId, campaignId, otherCampaignId);
    }

    [Fact]
    public async Task ListCallbacks_ShouldReturn404_WhenTheCampaignDoesNotExist()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        (await CountAsync("SELECT COUNT(*) FROM campaigns WHERE id = @Id", UnknownCampaignId))
            .Should().Be(0, "no campaign row has id {0}", UnknownCampaignId);

        using var response = await acme.GetAsync($"{CampaignsPath}/{UnknownCampaignId}/callbacks");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "no store holds campaign {0}", UnknownCampaignId);
    }

    [Fact]
    public async Task CreateCallback_ShouldReturn404AndScheduleNothing_WhenTheContactListTenantDiffersFromItsCampaignTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var contactId = await ContactIdOfListAsync(await SeedListAsync(Globex, campaignId));

        using var response = await globex.PostAsync($"{CampaignsPath}/{campaignId}/callbacks", CallbackBody(contactId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "contact {0} is of a globex list on acme's campaign {1}, so it is no contact of globex's campaign",
            contactId, campaignId);
        (await CountCallbacksAsync(contactId)).Should().Be(0, "nothing is scheduled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateCallback_ShouldReturn404AndScheduleNothing_WhenTheCampaignBelongsToAnotherTenant(
        bool dateIsUnreadable)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var campaignId = await CreateCampaignAsync(acme);
        var contactId = await ContactIdOfListAsync(await CreateListWithContactAsync(acme, campaignId));

        using var response = await globex.PostAsync($"{CampaignsPath}/{campaignId}/callbacks",
            CallbackBody(contactId, dateIsUnreadable ? UnreadableDate : null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "campaign {0} belongs to acme, so it is no campaign of globex's (unreadable date: {1})",
            campaignId, dateIsUnreadable);
        (await CountCallbacksAsync(contactId)).Should().Be(0, "nothing is scheduled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateCallback_ShouldReturn404AndScheduleNothing_WhenTheCampaignDoesNotExist(bool dateIsUnreadable)
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var contactId = await ContactIdOfListAsync(await CreateListWithContactAsync(acme, campaignId));

        using var response = await acme.PostAsync($"{CampaignsPath}/{UnknownCampaignId}/callbacks",
            CallbackBody(contactId, dateIsUnreadable ? UnreadableDate : null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "no store holds campaign {0} (unreadable date: {1})", UnknownCampaignId, dateIsUnreadable);
        (await CountCallbacksAsync(contactId)).Should().Be(0, "nothing is scheduled");
    }

    [Fact]
    public async Task CallbackRoutes_ShouldAnswerAsBefore_WhenTheCampaignAndContactBelongToTheTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        var campaignId = await CreateCampaignAsync(acme);
        var contactId = await ContactIdOfListAsync(await CreateListWithContactAsync(acme, campaignId));

        using var create = await acme.PostAsync($"{CampaignsPath}/{campaignId}/callbacks", CallbackBody(contactId));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        create.Headers.Location!.OriginalString.Should().Be($"/admin/campaigns/{campaignId}/callbacks");

        (await CallbackContactIdsAsync(acme, campaignId)).Should().Equal([contactId]);

        using var unreadable = await acme.PostAsync($"{CampaignsPath}/{campaignId}/callbacks",
            CallbackBody(contactId, UnreadableDate));
        unreadable.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountCallbacksAsync(contactId)).Should().Be(1, "only the first POST scheduled");
    }

    // ─── Postgres seeding and reads ──────────────────────────────────────────────

    /// <summary>A pending callback of <paramref name="tenantId"/> for <paramref name="contactId"/> on <paramref name="campaignId"/>, one day ahead.</summary>
    private async Task SeedCallbackAsync(string tenantId, long campaignId, long contactId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO scheduled_callbacks (tenant_id, campaign_id, contact_id, scheduled_at)
            VALUES (@T, @C, @K, NOW() + INTERVAL '1 day')
            """, conn);
        cmd.Parameters.AddWithValue("T", tenantId);
        cmd.Parameters.AddWithValue("C", campaignId);
        cmd.Parameters.AddWithValue("K", contactId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The one contact of list <paramref name="listId"/>.</summary>
    private async Task<long> ContactIdOfListAsync(long listId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id FROM dialer_contacts WHERE contact_list_id = @Id", conn);
        cmd.Parameters.AddWithValue("Id", listId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<long> CountCallbacksAsync(long contactId) =>
        CountAsync("SELECT COUNT(*) FROM scheduled_callbacks WHERE contact_id = @Id", contactId);

    /// <summary>A list of <paramref name="tenantId"/> on <paramref name="campaignId"/>, holding one contact.</summary>
    private async Task<long> SeedListAsync(string tenantId, long campaignId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            WITH l AS (INSERT INTO contact_lists (tenant_id, campaign_id, name) VALUES (@T, @C, 'mismatched') RETURNING id),
                 k AS (INSERT INTO dialer_contacts (tenant_id, contact_list_id, first_name)
                       SELECT @T, l.id, @F FROM l RETURNING id)
            INSERT INTO contact_phones (contact_id, phone_number) SELECT k.id, '+15551110000' FROM k
            RETURNING (SELECT id FROM l)
            """, conn);
        cmd.Parameters.AddWithValue("T", tenantId);
        cmd.Parameters.AddWithValue("C", campaignId);
        cmd.Parameters.AddWithValue("F", ContactFirstName);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>A disposition code of <paramref name="tenantId"/> on <paramref name="campaignId"/>.</summary>
    private async Task<long> SeedDispositionAsync(string tenantId, long campaignId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO disposition_codes (tenant_id, campaign_id, code, label) VALUES (@T, @C, 'MISMATCHED', 'Mismatched')
            RETURNING id
            """, conn);
        cmd.Parameters.AddWithValue("T", tenantId);
        cmd.Parameters.AddWithValue("C", campaignId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync(string sql, long id)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyList<string>> ContactFirstNamesAsync(long listId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT first_name FROM dialer_contacts WHERE contact_list_id = @Id ORDER BY id", conn);
        cmd.Parameters.AddWithValue("Id", listId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private async Task<(string TenantId, long CampaignId, string Code, string Label)?> DispositionRowAsync(long codeId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT tenant_id, campaign_id, code, label FROM disposition_codes WHERE id = @Id", conn);
        cmd.Parameters.AddWithValue("Id", codeId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return (reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3));
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}
