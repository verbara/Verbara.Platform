using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dnc-and-dispositions-are-tenant-scoped: through the HTTP pipeline, with two Customer tenants (<c>acme</c>,
/// <c>globex</c>) authenticated by real JWTs (<see cref="DialerTenantScopeFactory"/>). The stores are the in-memory ones, which follow the Pro
/// <c>2.20.3-pro</c> contract (an entry operation on a list the tenant does not own answers "no such list"; a
/// disposition write reports whether an attempt of the tenant matched). The same routes against Pro's Postgres stores
/// are <see cref="DncAndDispositionTenantScopeLiveDbTests"/>.
/// </summary>
public sealed class DncAndDispositionTenantScopeTests : IClassFixture<DialerTenantScopeFactory>
{
    private const string DncPath = "/api/admin/dnc-lists";
    private const string Phone = "573001234567";
    private const string Acme = DialerTenantScopeFactory.AcmeTenantId;
    private const string Globex = DialerTenantScopeFactory.GlobexTenantId;

    private readonly DialerTenantScopeFactory _factory;

    public DncAndDispositionTenantScopeTests(DialerTenantScopeFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string> EntryRoutes => new() { "list", "add", "remove", "import", "check" };

    [Theory]
    [MemberData(nameof(EntryRoutes))]
    public async Task DncEntryRoute_ShouldReturn404AndLeaveTheListUnchanged_WhenTheListBelongsToAnotherTenant(string route)
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var listId = await CreateListWithEntryAsync(acme);

        using var response = await SendEntryRouteAsync(globex, route, listId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "list {0} belongs to acme, so it is no list of globex's", listId);
        (await EntriesAsync(acme, listId)).Should().Equal([Phone],
            "a route of another tenant neither adds, removes nor imports an entry");
    }

    [Theory]
    [MemberData(nameof(EntryRoutes))]
    public async Task DncEntryRoute_ShouldSucceed_WhenTheListBelongsToTheTenant(string route)
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var listId = await CreateListWithEntryAsync(acme);

        using var response = await SendEntryRouteAsync(acme, route, listId);

        response.IsSuccessStatusCode.Should().BeTrue("acme owns list {0}", listId);
    }

    [Fact]
    public async Task CreateDncList_ShouldReturn403AndCreateNothing_WhenATenantAdministratorAsksForGlobalScope()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var name = $"dnc-{Guid.NewGuid():N}";

        using var response = await acme.PostAsync(DncPath, JsonContent.Create(new { name, scope = "Global" }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ListNamesAsync(acme)).Should().NotContain(name);
    }

    [Fact]
    public async Task UpdateDncList_ShouldReturn403AndChangeNothing_WhenATenantAdministratorAsksForGlobalScope()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var listId = await CreateListAsync(acme);

        using var response = await acme.PutAsync($"{DncPath}/{listId}",
            JsonContent.Create(new { name = "renamed", scope = "global" }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var list = JsonNode.Parse(await acme.GetStringAsync($"{DncPath}/{listId}"))!;
        list["scope"]!.GetValue<string>().Should().Be("Tenant");
        list["name"]!.GetValue<string>().Should().NotBe("renamed");
    }

    [Fact]
    public async Task UpdateCallAttemptDisposition_ShouldReturn404AndChangeNothing_WhenTheAttemptBelongsToAnotherTenant()
    {
        var store = _factory.Services.GetRequiredService<InMemoryCampaignStore>();
        var attemptId = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        store.SeedCallAttempt(attemptId, Acme);
        using var globex = _factory.CreateAdminClient(Globex);

        using var response = await globex.PutAsync($"/api/admin/call-attempts/{attemptId}/disposition",
            JsonContent.Create(new { dispositionId = 5, agentComment = "not mine" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        store.RecordedDispositionUpdates.Should().NotContain(u => u.CallAttemptId == attemptId);
    }

    [Fact]
    public async Task UpdateCallAttemptDisposition_ShouldReturn204AndStoreIt_WhenTheAttemptBelongsToTheTenant()
    {
        var store = _factory.Services.GetRequiredService<InMemoryCampaignStore>();
        var attemptId = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        store.SeedCallAttempt(attemptId, Acme);
        using var acme = _factory.CreateAdminClient(Acme);

        using var response = await acme.PutAsync($"/api/admin/call-attempts/{attemptId}/disposition",
            JsonContent.Create(new { dispositionId = 5, agentComment = "callback tomorrow" }));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var update = store.RecordedDispositionUpdates.Should().ContainSingle(u => u.CallAttemptId == attemptId).Which;
        update.TenantId.Should().Be(Acme);
        update.DispositionId.Should().Be(5);
        update.AgentComment.Should().Be("callback tomorrow");
    }

    // ─── Helpers (shared with the Live-DB suite) ─────────────────────────────────

    internal static async Task<HttpResponseMessage> SendEntryRouteAsync(HttpClient client, string route, long listId) =>
        route switch
        {
            "list" => await client.GetAsync($"{DncPath}/{listId}/entries"),
            "add" => await client.PostAsync($"{DncPath}/{listId}/entries",
                JsonContent.Create(new { phoneNumber = "573009990000", reason = "opt-out" })),
            "remove" => await client.DeleteAsync($"{DncPath}/{listId}/entries/{Phone}"),
            "import" => await client.PostAsync($"{DncPath}/{listId}/import", CsvUpload("573009990001\n573009990002")),
            "check" => await client.GetAsync($"{DncPath}/{listId}/check/{Phone}"),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };

    internal static async Task<long> CreateListAsync(HttpClient client)
    {
        using var response = await client.PostAsync(DncPath, JsonContent.Create(new { name = $"dnc-{Guid.NewGuid():N}" }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    internal static async Task<long> CreateListWithEntryAsync(HttpClient client)
    {
        var listId = await CreateListAsync(client);
        using var response = await client.PostAsync($"{DncPath}/{listId}/entries",
            JsonContent.Create(new { phoneNumber = Phone, reason = "opt-out" }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return listId;
    }

    internal static async Task<IReadOnlyList<string>> EntriesAsync(HttpClient client, long listId)
    {
        var items = JsonNode.Parse(await client.GetStringAsync($"{DncPath}/{listId}/entries?limit=100"))!.AsArray();
        return items.Select(n => n!["phoneNumber"]!.GetValue<string>()).ToList();
    }

    internal static async Task<IReadOnlyList<string>> ListNamesAsync(HttpClient client)
    {
        var items = JsonNode.Parse(await client.GetStringAsync(DncPath))!.AsArray();
        return items.Select(n => n!["name"]!.GetValue<string>()).ToList();
    }

    private static MultipartFormDataContent CsvUpload(string csv)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "dnc.csv");
        return form;
    }
}
