using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Routing;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-references-stay-in-their-tenant: an outbound route's create and update name only trunks of the caller's
/// tenant, through the HTTP pipeline with two Customer tenants (<c>acme</c>, <c>globex</c>) authenticated by real JWTs
/// (<see cref="DialerTenantScopeFactory"/>). The stores are the tenant-aware in-memory ones, which accept any trunk id;
/// <see cref="DialerOutboundRouteTenantScopeLiveDbTests"/> runs the same scenarios against Pro's Postgres stores.
/// </summary>
public sealed class DialerOutboundRouteTenantScopeTests
    : OutboundRouteTenantScopeScenarios, IClassFixture<DialerTenantScopeFactory>
{
    private readonly DialerTenantScopeFactory _factory;

    public DialerOutboundRouteTenantScopeTests(DialerTenantScopeFactory factory)
    {
        _factory = factory;
    }

    /// <inheritdoc />
    protected override DialerTenantScopeFactory Factory => _factory;

    // In memory a trunk named by a route's trunkId can be deleted (no foreign key), so only this host can store a
    // route whose trunkId names no trunk; the live-DB host refuses that delete.
    [Fact]
    public async Task UpdateRoute_ShouldReturn200_WhenAWholeRouteEditResendsATrunkIdThatNamesADeletedTrunk()
    {
        using var globex = Factory.CreateAdminClient(Globex);
        var trunk = await CreateTrunkAsync(globex);
        var routeId = await CreateRouteAsync(globex, trunk, overflowTrunkId: null);
        await DeleteTrunkAsync(globex, trunk);
        var pattern = NewPattern();

        using var response = await globex.PutAsync($"{RoutesPath}/{routeId}",
            RouteBody(pattern, trunk, overflowTrunkId: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "trunkId {0} is the value route {1} already stores, so the edit adds no trunk reference ({2})",
            trunk, routeId, await response.Content.ReadAsStringAsync());
        JsonNode.Parse(await ReadRouteAsync(globex, routeId))!["pattern"]!.GetValue<string>().Should().Be(pattern);
    }

    // Pro 2.33.0-pro: the route store writes no row and answers 0 when a trunk is not one of the tenant's at the write.
    // The endpoint's trunk check runs first, so only a trunk deleted between that check and the write reaches it; the
    // in-memory store is told to refuse the next create to stand for that window.
    [Fact]
    public async Task CreateRoute_ShouldReturn400AndCreateNoRoute_WhenTheStoreRefusesTheWriteAfterTheTrunkCheck()
    {
        using var globex = Factory.CreateAdminClient(Globex);
        var trunk = await CreateTrunkAsync(globex);
        var pattern = NewPattern();
        var routes = (InMemoryOutboundRouteStore)_factory.Services.GetRequiredService<OutboundRouteStoreBase>();
        routes.RefuseNextCreate();

        using var response = await globex.PostAsync(RoutesPath, RouteBody(pattern, trunk, overflowTrunkId: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the store wrote no route, so there is no route to answer 201 with ({0})",
            await response.Content.ReadAsStringAsync());
        await AssertErrorResponseAsync(response);
        (await RoutePatternsAsync(globex)).Should().NotContain(pattern);
        var audit = _factory.Services.GetRequiredService<IAuditStore>();
        (await audit.GetByEntityAsync(new TenantId(Globex), "outbound_route", "0", CancellationToken.None))
            .Should().BeEmpty("no route was written, so none is audited as created");
    }

    // Pro 2.33.0-pro: the route store changes no row, and reports nothing, when a new trunk is not one of the tenant's
    // at the write. The endpoint reads the route back; the in-memory store is told to refuse the next update to stand
    // for a trunk deleted between the endpoint's check and the write.
    [Fact]
    public async Task UpdateRoute_ShouldReturn400AndKeepTheRoute_WhenTheStoreRefusesTheWriteAfterTheTrunkCheck()
    {
        using var globex = Factory.CreateAdminClient(Globex);
        var trunk = await CreateTrunkAsync(globex);
        var newTrunk = await CreateTrunkAsync(globex);
        var routeId = await CreateRouteAsync(globex, trunk, overflowTrunkId: null);
        var routes = (InMemoryOutboundRouteStore)_factory.Services.GetRequiredService<OutboundRouteStoreBase>();
        routes.RefuseNextUpdate();

        using var response = await globex.PutAsync($"{RoutesPath}/{routeId}", JsonContent.Create(new { trunkId = newTrunk }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the store changed nothing, so the route still names trunk {0} ({1})",
            trunk, await response.Content.ReadAsStringAsync());
        await AssertErrorResponseAsync(response);
        JsonNode.Parse(await ReadRouteAsync(globex, routeId))!["trunkId"]!.GetValue<long>().Should().Be(trunk);
        var audit = _factory.Services.GetRequiredService<IAuditStore>();
        (await audit.GetByEntityAsync(new TenantId(Globex), "outbound_route",
                routeId.ToString(CultureInfo.InvariantCulture), CancellationToken.None))
            .Should().NotContain(e => e.Action == "outbound_route.updated", "no change was written, so none is audited");
    }
}

/// <summary>
/// The scenarios of the requirement "An outbound route names only trunks of the caller's tenant", shared by the
/// in-memory and the live-DB host. Every assertion goes through HTTP, so both hosts answer the same test.
/// </summary>
public abstract class OutboundRouteTenantScopeScenarios
{
    /// <summary>The route group.</summary>
    protected const string RoutesPath = "/api/v1/admin/routes";

    /// <summary>The trunk group.</summary>
    protected const string TrunksPath = "/api/v1/admin/trunks";

    /// <summary>A trunk id no trunk has.</summary>
    protected const long MissingTrunkId = 999_999;

    /// <summary>The tenant that owns the trunk another tenant names.</summary>
    protected const string Acme = DialerTenantScopeFactory.AcmeTenantId;

    /// <summary>The tenant that names another tenant's trunk.</summary>
    protected const string Globex = DialerTenantScopeFactory.GlobexTenantId;

    /// <summary>The host the scenarios run on.</summary>
    protected abstract DialerTenantScopeFactory Factory { get; }

    // ─── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRoute_ShouldReturn400AndCreateNoRoute_WhenTheTrunkBelongsToAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        var pattern = NewPattern();

        using var response = await globex.PostAsync(RoutesPath, RouteBody(pattern, acmeTrunk, overflowTrunkId: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "trunk {0} belongs to acme, so it is no trunk of globex's", acmeTrunk);
        await AssertErrorResponseAsync(response);
        (await RoutePatternsAsync(globex)).Should().NotContain(pattern, "no route of globex is written on acme's trunk");
    }

    [Fact]
    public async Task CreateRoute_ShouldAnswerTheSame_WhenTheTrunkBelongsToAnotherTenantOrDoesNotExist()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        var foreignPattern = NewPattern();
        var missingPattern = NewPattern();

        using var foreign = await globex.PostAsync(RoutesPath, RouteBody(foreignPattern, acmeTrunk, overflowTrunkId: null));
        using var missing = await globex.PostAsync(RoutesPath, RouteBody(missingPattern, MissingTrunkId, overflowTrunkId: null));

        await AssertSameRefusalAsync(foreign, missing, "trunkId", acmeTrunk);
        var patterns = await RoutePatternsAsync(globex);
        patterns.Should().NotContain(foreignPattern);
        patterns.Should().NotContain(missingPattern);
    }

    [Fact]
    public async Task CreateRoute_ShouldReturn400AndCreateNoRoute_WhenTheOverflowTrunkBelongsToAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        var globexTrunk = await CreateTrunkAsync(globex);
        var pattern = NewPattern();

        using var response = await globex.PostAsync(RoutesPath, RouteBody(pattern, globexTrunk, acmeTrunk));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "overflow trunk {0} belongs to acme, so it is no trunk of globex's", acmeTrunk);
        await AssertErrorResponseAsync(response);
        (await RoutePatternsAsync(globex)).Should().NotContain(pattern);
    }

    [Fact]
    public async Task CreateRoute_ShouldAnswerTheSame_WhenTheOverflowTrunkBelongsToAnotherTenantOrDoesNotExist()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        var globexTrunk = await CreateTrunkAsync(globex);
        var foreignPattern = NewPattern();
        var missingPattern = NewPattern();

        using var foreign = await globex.PostAsync(RoutesPath, RouteBody(foreignPattern, globexTrunk, acmeTrunk));
        using var missing = await globex.PostAsync(RoutesPath, RouteBody(missingPattern, globexTrunk, MissingTrunkId));

        await AssertSameRefusalAsync(foreign, missing, "overflowTrunkId", acmeTrunk);
        var patterns = await RoutePatternsAsync(globex);
        patterns.Should().NotContain(foreignPattern);
        patterns.Should().NotContain(missingPattern);
    }

    // ─── Update ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateRoute_ShouldReturn400AndLeaveTheRouteUnchanged_WhenTheTrunkBelongsToAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        var globexTrunk = await CreateTrunkAsync(globex);
        var routeId = await CreateRouteAsync(globex, globexTrunk, overflowTrunkId: null);
        var before = await ReadRouteAsync(globex, routeId);

        using var response = await globex.PutAsync($"{RoutesPath}/{routeId}", JsonContent.Create(new { trunkId = acmeTrunk }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "trunk {0} belongs to acme, so route {1} cannot move onto it", acmeTrunk, routeId);
        await AssertErrorResponseAsync(response);
        var after = await ReadRouteAsync(globex, routeId);
        after.Should().Be(before, "a refused update changes no field of the route");
        JsonNode.Parse(after)!["trunkId"]!.GetValue<long>().Should().Be(globexTrunk);
    }

    [Fact]
    public async Task UpdateRoute_ShouldAnswerTheSame_WhenTheTrunkBelongsToAnotherTenantOrDoesNotExist()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        var globexTrunk = await CreateTrunkAsync(globex);
        var routeId = await CreateRouteAsync(globex, globexTrunk, overflowTrunkId: null);
        var before = await ReadRouteAsync(globex, routeId);

        using var foreign = await globex.PutAsync($"{RoutesPath}/{routeId}", JsonContent.Create(new { trunkId = acmeTrunk }));
        var afterForeign = await ReadRouteAsync(globex, routeId);
        using var missing = await globex.PutAsync($"{RoutesPath}/{routeId}", JsonContent.Create(new { trunkId = MissingTrunkId }));
        var afterMissing = await ReadRouteAsync(globex, routeId);

        await AssertSameRefusalAsync(foreign, missing, "trunkId", acmeTrunk);
        afterForeign.Should().Be(before, "the update to acme's trunk changes no field of the route");
        afterMissing.Should().Be(before, "the update to a missing trunk changes no field of the route");
    }

    [Fact]
    public async Task UpdateRoute_ShouldAnswerTheSame_WhenTheOverflowTrunkBelongsToAnotherTenantOrDoesNotExist()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        var globexTrunk = await CreateTrunkAsync(globex);
        var routeId = await CreateRouteAsync(globex, globexTrunk, overflowTrunkId: null);
        var before = await ReadRouteAsync(globex, routeId);

        using var foreign = await globex.PutAsync($"{RoutesPath}/{routeId}",
            JsonContent.Create(new { overflowTrunkId = acmeTrunk }));
        var afterForeign = await ReadRouteAsync(globex, routeId);
        using var missing = await globex.PutAsync($"{RoutesPath}/{routeId}",
            JsonContent.Create(new { overflowTrunkId = MissingTrunkId }));
        var afterMissing = await ReadRouteAsync(globex, routeId);

        await AssertSameRefusalAsync(foreign, missing, "overflowTrunkId", acmeTrunk);
        afterForeign.Should().Be(before, "the overflow update to acme's trunk changes no field of the route");
        afterMissing.Should().Be(before, "the overflow update to a missing trunk changes no field of the route");
        JsonNode.Parse(afterMissing)!["overflowTrunkId"].Should().BeNull("the route still has no overflow trunk");
    }

    // ─── Whole-route edits (design D2: an unchanged trunk id is no new reference) ─────

    [Fact]
    public async Task UpdateRoute_ShouldReturn200_WhenAWholeRouteEditResendsAnOverflowTrunkIdThatNamesADeletedTrunk()
    {
        using var globex = Factory.CreateAdminClient(Globex);
        var trunk = await CreateTrunkAsync(globex);
        var overflow = await CreateTrunkAsync(globex);
        var routeId = await CreateRouteAsync(globex, trunk, overflow);
        await DeleteTrunkAsync(globex, overflow);
        var pattern = NewPattern();

        using var response = await globex.PutAsync($"{RoutesPath}/{routeId}", RouteBody(pattern, trunk, overflow));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "overflowTrunkId {0} is the value route {1} already stores, so the edit adds no trunk reference ({2})",
            overflow, routeId, await response.Content.ReadAsStringAsync());
        var route = JsonNode.Parse(await ReadRouteAsync(globex, routeId))!;
        route["pattern"]!.GetValue<string>().Should().Be(pattern, "the edited field is written");
        route["overflowTrunkId"]!.GetValue<long>().Should().Be(overflow);
    }

    [Fact]
    public async Task UpdateRoute_ShouldReturn400AndLeaveTheRouteUnchanged_WhenAWholeRouteEditChangesTheOverflowTrunkToAnotherTenantsTrunk()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        var trunk = await CreateTrunkAsync(globex);
        var overflow = await CreateTrunkAsync(globex);
        var routeId = await CreateRouteAsync(globex, trunk, overflow);
        await DeleteTrunkAsync(globex, overflow);
        var before = await ReadRouteAsync(globex, routeId);

        using var response = await globex.PutAsync($"{RoutesPath}/{routeId}", RouteBody(NewPattern(), trunk, acmeTrunk));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "overflowTrunkId changes to {0}, which is no trunk of globex's", acmeTrunk);
        await AssertErrorResponseAsync(response);
        (await ReadRouteAsync(globex, routeId)).Should().Be(before, "a refused update changes no field of the route");
    }

    [Fact]
    public async Task UpdateRoute_ShouldReturn200_WhenAWholeRouteEditResendsTheStoredTrunks()
    {
        using var globex = Factory.CreateAdminClient(Globex);
        var trunk = await CreateTrunkAsync(globex);
        var overflow = await CreateTrunkAsync(globex);
        var routeId = await CreateRouteAsync(globex, trunk, overflow);
        var pattern = NewPattern();

        using var response = await globex.PutAsync($"{RoutesPath}/{routeId}", RouteBody(pattern, trunk, overflow));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        JsonNode.Parse(await ReadRouteAsync(globex, routeId))!["pattern"]!.GetValue<string>().Should().Be(pattern);
    }

    [Fact]
    public async Task UpdateRoute_ShouldReturn400AndLeaveTheRouteUnchanged_WhenAWholeRouteEditChangesTheTrunkToAnotherTenantsTrunk()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        var trunk = await CreateTrunkAsync(globex);
        var routeId = await CreateRouteAsync(globex, trunk, overflowTrunkId: null);
        var before = await ReadRouteAsync(globex, routeId);

        using var response = await globex.PutAsync($"{RoutesPath}/{routeId}",
            RouteBody(NewPattern(), acmeTrunk, overflowTrunkId: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "trunkId changes to {0}, which is no trunk of globex's", acmeTrunk);
        await AssertErrorResponseAsync(response);
        (await ReadRouteAsync(globex, routeId)).Should().Be(before, "a refused update changes no field of the route");
    }

    // ─── Own trunks ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteCreateAndUpdate_ShouldAnswerAsBefore_WhenTheTrunksBelongToTheTenant()
    {
        using var globex = Factory.CreateAdminClient(Globex);
        var trunk8 = await CreateTrunkAsync(globex);
        var trunk9 = await CreateTrunkAsync(globex);

        using var create = await globex.PostAsync(RoutesPath, RouteBody(NewPattern(), trunk8, trunk9));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var routeId = JsonNode.Parse(await create.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();

        using var update = await globex.PutAsync($"{RoutesPath}/{routeId}", JsonContent.Create(new { trunkId = trunk9 }));
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var route = JsonNode.Parse(await ReadRouteAsync(globex, routeId))!;
        route["trunkId"]!.GetValue<long>().Should().Be(trunk9);
        route["overflowTrunkId"]!.GetValue<long>().Should().Be(trunk9);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Both refusals are 400 with the same <c>ErrorResponse</c> body, byte for byte.</summary>
    protected static async Task AssertSameRefusalAsync(
        HttpResponseMessage foreign, HttpResponseMessage missing, string field, long foreignTrunkId)
    {
        var foreignBody = await foreign.Content.ReadAsStringAsync();
        var missingBody = await missing.Content.ReadAsStringAsync();
        ((int)foreign.StatusCode, (int)missing.StatusCode).Should().Be((400, 400),
            "{0} {1} belongs to another tenant and {2} names no trunk; both are no trunk of the caller's " +
            "(foreign answered {3} {4}, missing answered {5} {6})",
            field, foreignTrunkId, MissingTrunkId,
            (int)foreign.StatusCode, foreignBody, (int)missing.StatusCode, missingBody);
        missingBody.Should().Be(foreignBody, "the answer must not tell a foreign trunk from a missing one");
        await AssertErrorResponseAsync(foreign);
    }

    /// <summary>The body is the <c>ErrorResponse</c> shape, <c>{"error": "…"}</c>.</summary>
    protected static async Task AssertErrorResponseAsync(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Select(kv => kv.Key).Should().Equal("error");
    }

    /// <summary>Creates a trunk of the client's tenant through the API and returns its id.</summary>
    protected static async Task<long> CreateTrunkAsync(HttpClient client)
    {
        using var response = await client.PostAsync(TrunksPath, JsonContent.Create(new
        {
            name = $"trk-{Guid.NewGuid():N}"[..20],
            displayName = default(string),
            type = "pjsip",
            isActive = true,
            maxChannels = 10,
            transport = "transport-udp",
            codecs = "ulaw,alaw",
            authUsername = default(string),
            authPassword = default(string),
            registrationUri = default(string),
            clientUri = default(string),
            context = "from-trunk",
            matchHost = default(string),
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    /// <summary>Deletes a trunk of the client's tenant through the API.</summary>
    protected static async Task DeleteTrunkAsync(HttpClient client, long trunkId)
    {
        using var response = await client.DeleteAsync($"{TrunksPath}/{trunkId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Creates a route of the client's tenant on its own trunk and returns its id.</summary>
    protected static async Task<long> CreateRouteAsync(HttpClient client, long trunkId, long? overflowTrunkId)
    {
        using var response = await client.PostAsync(RoutesPath, RouteBody(NewPattern(), trunkId, overflowTrunkId));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    /// <summary>The route as its tenant reads it, as raw JSON.</summary>
    protected static async Task<string> ReadRouteAsync(HttpClient client, long routeId)
    {
        using var response = await client.GetAsync($"{RoutesPath}/{routeId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "route {0} is the caller's own", routeId);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The patterns of the client's tenant's routes.</summary>
    protected static async Task<IReadOnlyList<string>> RoutePatternsAsync(HttpClient client)
    {
        using var response = await client.GetAsync(RoutesPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .Select(n => n!["pattern"]!.GetValue<string>())];
    }

    /// <summary>A route create body.</summary>
    protected static JsonContent RouteBody(string pattern, long trunkId, long? overflowTrunkId) => JsonContent.Create(new
    {
        campaignId = default(long?),
        pattern,
        patternType = "prefix",
        trunkId,
        overflowTrunkId,
        dialPrefix = default(string),
        priority = 1,
    });

    /// <summary>A dial pattern no other test uses, so a route is found by it.</summary>
    protected static string NewPattern() =>
        "9" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);
}
