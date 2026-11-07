using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.AgentAssist.Storage.Postgres.Stores;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-sessions-stay-in-their-tenant (spec <c>supervisor-silent-listen</c>, design D1-D5): Agent Assist live
/// sessions, whisper, the transcript listen and the stored suggestion and compliance records stay in their tenant.
/// <list type="bullet">
/// <item><b>Regression tests (task 1.2)</b>, written against the code of task 1.1, where every route reads the seams
/// with no tenant filter and the listen records the body's supervisor id: each fails on that code.</item>
/// <item><b>Allowed-caller tests (task 1.3)</b>, green before and after the fix.</item>
/// </list>
/// Every test names its own session ids, so the shared host's sessions and rows never decide another test.
/// </summary>
public sealed class AgentAssistTenantScopeTests : IClassFixture<AgentAssistTenantScopeApiFactory>
{
    private const string Acme = AgentAssistTenantScopeApiFactory.Acme;
    private const string Globex = AgentAssistTenantScopeApiFactory.Globex;
    private const string ActiveRoute = "/api/v1/supervisor/sessions/active";

    private readonly AgentAssistTenantScopeApiFactory _factory;

    public AgentAssistTenantScopeTests(AgentAssistTenantScopeApiFactory factory) => _factory = factory;

    private static string NewId(string label) => $"aa-{label}-{Guid.NewGuid():N}";

    private static string WhisperRoute(string sessionId) => $"/api/v1/supervisor/sessions/{sessionId}/whisper";

    private static string ListenRoute(string sessionId) => $"/api/v1/supervisor/sessions/{sessionId}/listen";

    private static CallSession Call(string linkedId, string? tenant)
    {
        var call = new CallSession($"s-{linkedId}", linkedId, "primary", CallDirection.Inbound)
        {
            AgentId = "0651f84b6f284a1c9b8d658330fa8310",
            QueueName = "support",
            ConnectedAt = new DateTimeOffset(2026, 11, 6, 15, 1, 0, TimeSpan.Zero),
        };
        if (tenant is not null)
            call.TenantId = tenant;
        return call;
    }

    /// <summary>A live session owned by <paramref name="tenant"/>, its call stamped with the same tenant.</summary>
    private string AddSession(string tenant, string label, bool deliversWhispers = true)
    {
        var id = NewId(label);
        _factory.Live.Add(AgentAssistLiveSessions.ToLive(id, Call($"{id}.call", tenant), tenant), deliversWhispers);
        return id;
    }

    private static async Task<JsonArray> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(ActiveRoute);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {body}");
        return JsonNode.Parse(body)!.AsArray();
    }

    private static IEnumerable<string> SessionIds(JsonArray items) => items.Select(i => i!["sessionId"]!.GetValue<string>());

    private static IReadOnlyList<string> ListenKeysOf(string sessionId) =>
        [.. SupervisorListenStore.Sessions.Keys.Where(k => k.Contains(sessionId, StringComparison.Ordinal))];

    private static SuggestionLogRow Suggestion(long id, string sessionId, string tenant) => new()
    {
        Id = id,
        SessionId = sessionId,
        TenantId = tenant,
        EmittedAt = new DateTimeOffset(2026, 11, 6, 15, 2, 0, TimeSpan.Zero).AddSeconds(id),
        Priority = "Normal",
        Source = "Keyword",
        TriggerPhrase = "refund",
        SuggestionText = $"suggestion {id} of {tenant}",
        Whispered = false,
    };

    private static ComplianceAlertRow Alert(long id, string sessionId, string tenant) => new()
    {
        Id = id,
        SessionId = sessionId,
        TenantId = tenant,
        OccurredAt = new DateTimeOffset(2026, 11, 6, 15, 3, 0, TimeSpan.Zero).AddSeconds(id),
        RuleId = "pci-card-number",
        Phrase = "card number",
        Severity = "High",
    };

    private static async Task<JsonArray> GetRowsAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {body}");
        return JsonNode.Parse(body)!.AsArray();
    }

    /// <summary>
    /// A listen POST with a zero-length body and, when <paramref name="contentType"/> is given, that content type —
    /// the Monitor page's <c>customFetch</c> sends <c>application/json</c> with no body. Asserts what it sends.
    /// </summary>
    private static async Task<HttpResponseMessage> PostEmptyListenAsync(HttpClient client, string sessionId, string? contentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ListenRoute(sessionId)) { Content = new ByteArrayContent([]) };
        if (contentType is not null)
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        request.Content.Headers.ContentType?.MediaType.Should().Be(contentType);
        if (contentType is null)
            request.Content.Headers.ContentType.Should().BeNull("this shape carries no content type");
        (await request.Content.ReadAsByteArrayAsync()).Should().BeEmpty("the body is zero-length");

        return await client.SendAsync(request);
    }

    // ═══ Task 1.2: regression tests, red on the code of task 1.1 ════════════════

    [Fact]
    public async Task GetActiveSessions_ShouldListOnlyTheCallersTenant_WhenTwoTenantsHaveLiveSessions()
    {
        var acmeSession = AddSession(Acme, "two-tenants-acme");
        var globexSession = AddSession(Globex, "two-tenants-globex");
        using var acme = _factory.SupervisorClient(Acme);
        using var globex = _factory.SupervisorClient(Globex);

        var acmeIds = SessionIds(await ListAsync(acme)).ToList();
        var globexIds = SessionIds(await ListAsync(globex)).ToList();

        acmeIds.Should().Contain(acmeSession).And.NotContain(globexSession, "acme's supervisor never sees globex's session");
        globexIds.Should().Contain(globexSession).And.NotContain(acmeSession, "globex's supervisor never sees acme's session");
    }

    [Fact]
    public async Task GetActiveSessions_ShouldOmitTheSession_WhenItsTenantIsUnresolved()
    {
        var unresolved = NewId("unresolved");
        _factory.Live.Add(AgentAssistLiveSessions.ToLive(unresolved, Call($"{unresolved}.call", tenant: null), sessionTenant: ""));
        using var acme = _factory.SupervisorClient(Acme);

        SessionIds(await ListAsync(acme)).Should().NotContain(unresolved, "a session no tenant owns is listed for nobody");
    }

    [Fact]
    public async Task GetActiveSessions_ShouldOmitTheSession_WhenCallAndSnapshotTenantsDiffer()
    {
        var conflicted = NewId("conflicted");
        var live = AgentAssistLiveSessions.ToLive(conflicted, Call($"{conflicted}.call", Acme), sessionTenant: Globex);
        live.OwnerTenant.Should().BeNull("the adapter maps a conflict to no owner");
        _factory.Live.Add(live);
        using var acme = _factory.SupervisorClient(Acme);
        using var globex = _factory.SupervisorClient(Globex);

        SessionIds(await ListAsync(acme)).Should().NotContain(conflicted, "a conflicted session is listed for neither tenant");
        SessionIds(await ListAsync(globex)).Should().NotContain(conflicted, "a conflicted session is listed for neither tenant");
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer404AndQueueNothing_WhenTheSessionBelongsToAnotherTenant()
    {
        var globexSession = AddSession(Globex, "whisper-foreign");
        using var acme = _factory.SupervisorClient(Acme);

        using var response = await acme.PostAsJsonAsync(WhisperRoute(globexSession), new { text = "offer the refund" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, because: "another tenant's session answers as an unknown one");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        _factory.Live.Whispers.Should().NotContain(w => w.SessionId == globexSession, "no whisper is queued for it");
    }

    [Fact]
    public async Task PostListen_ShouldAnswer404AndRecordNothing_WhenTheSessionBelongsToAnotherTenant()
    {
        var globexSession = AddSession(Globex, "listen-foreign");
        using var acme = _factory.SupervisorClient(Acme);

        using var response = await acme.PostAsJsonAsync(ListenRoute(globexSession), new { supervisorId = "someone" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, because: "another tenant's session answers as an unknown one");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        ListenKeysOf(globexSession).Should().BeEmpty("no listen entry is recorded for it");
    }

    [Fact]
    public async Task PostListen_ShouldRecordTheCaller_WhenTheBodyNamesAnotherSupervisor()
    {
        var acmeSession = AddSession(Acme, "listen-impersonated");
        var caller = _factory.SupervisorOf(Acme).UserId.Value;
        using var acme = _factory.SupervisorClient(Acme);

        using var response = await acme.PostAsJsonAsync(ListenRoute(acmeSession), new { supervisorId = "another-supervisor" });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {body}");
        JsonNode.Parse(body)!["supervisorId"]!.GetValue<string>().Should().Be(caller, "the signed-in supervisor is recorded");
        var recorded = ListenKeysOf(acmeSession).Select(k => SupervisorListenStore.Sessions[k].SupervisorId).ToList();
        recorded.Should().Equal([caller], "the only entry recorded for the session names the caller, never the body's id");
    }

    /// <summary>
    /// Audit F2 (spec "A recorded listen MUST be kept apart per tenant"): the same session id and the same user id,
    /// recorded under two tenants one after the other, leave two entries. Without the tenant in the store key the
    /// second listen overwrites the first and one entry remains.
    /// </summary>
    [Fact]
    public async Task PostListen_ShouldKeepOneEntryPerTenant_WhenTheSameUserIdListensToTheSameSessionIdInTwoTenants()
    {
        var sessionId = NewId("listen-two-tenants");
        var sharedUserId = $"shared-supervisor-{Guid.NewGuid():N}";
        var acmeUser = _factory.SaveUser(sharedUserId, Acme, UserRole.Supervisor, UserStatus.Active);
        var globexUser = _factory.SaveUser(sharedUserId, Globex, UserRole.Supervisor, UserStatus.Active);

        _factory.Live.Add(AgentAssistLiveSessions.ToLive(sessionId, Call($"{sessionId}.acme", Acme), Acme));
        using (var acme = _factory.CreateBearerClient(_factory.MintAccessToken(acmeUser)))
        using (var response = await acme.PostAsync(ListenRoute(sessionId), content: null))
            response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {await response.Content.ReadAsStringAsync()}");

        _factory.Live.Add(AgentAssistLiveSessions.ToLive(sessionId, Call($"{sessionId}.globex", Globex), Globex));
        using (var globex = _factory.CreateBearerClient(_factory.MintAccessToken(globexUser)))
        using (var response = await globex.PostAsync(ListenRoute(sessionId), content: null))
            response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {await response.Content.ReadAsStringAsync()}");

        var recorded = ListenKeysOf(sessionId).Select(k => SupervisorListenStore.Sessions[k].SupervisorId).ToList();
        recorded.Should().Equal([sharedUserId, sharedUserId],
            "each tenant's listen is its own entry; the second tenant's listen never replaces the first one's");
    }

    [Fact]
    public async Task PostListen_ShouldAnswer200_WhenTheBodyIsEmptyWithAJsonContentType()
    {
        var acmeSession = AddSession(Acme, "listen-json-empty");
        var caller = _factory.SupervisorOf(Acme).UserId.Value;
        using var acme = _factory.SupervisorClient(Acme);

        using var response = await PostEmptyListenAsync(acme, acmeSession, "application/json");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the console's bodyless JSON request reaches the handler; the response was {body}");
        JsonNode.Parse(body)!["supervisorId"]!.GetValue<string>().Should().Be(caller);
    }

    [Fact]
    public async Task PostListen_ShouldAnswer200_WhenTheRequestHasNoBodyAndNoContentType()
    {
        var acmeSession = AddSession(Acme, "listen-no-type");
        var caller = _factory.SupervisorOf(Acme).UserId.Value;
        using var acme = _factory.SupervisorClient(Acme);

        using var response = await PostEmptyListenAsync(acme, acmeSession, contentType: null);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"a bodyless request reaches the handler; the response was {body}");
        JsonNode.Parse(body)!["supervisorId"]!.GetValue<string>().Should().Be(caller);
    }

    [Fact]
    public async Task PostListen_ShouldAnswer403AndRecordNothing_WhenTheCredentialsCarryNoUserId()
    {
        var acmeSession = AddSession(Acme, "listen-no-user");
        using var client = _factory.SupervisorWithoutUserIdClient();

        using var response = await client.PostAsJsonAsync(ListenRoute(acmeSession), new { supervisorId = "someone" });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: $"no user id means no one to record; the response was {body}");
        body.Should().BeEmpty();
        ListenKeysOf(acmeSession).Should().BeEmpty("nothing is recorded");
    }

    [Fact]
    public async Task GetSessionSuggestions_ShouldReturnEmpty_WhenTheRowsBelongToAnotherTenant()
    {
        var globexSession = NewId("suggestions-foreign");
        _factory.Records.Add(Suggestion(1, globexSession, Globex));
        _factory.Records.Add(Suggestion(2, globexSession, Globex));
        using var acme = _factory.SupervisorClient(Acme);

        (await GetRowsAsync(acme, $"/api/v1/agent-assist/sessions/{globexSession}/suggestions"))
            .Should().BeEmpty("another tenant's rows answer as a session with no rows");
    }

    [Fact]
    public async Task GetSessionCompliance_ShouldReturnEmpty_WhenTheRowsBelongToAnotherTenant()
    {
        var globexSession = NewId("compliance-foreign");
        _factory.Records.Add(Alert(1, globexSession, Globex));
        using var acme = _factory.SupervisorClient(Acme);

        (await GetRowsAsync(acme, $"/api/v1/agent-assist/sessions/{globexSession}/compliance"))
            .Should().BeEmpty("another tenant's rows answer as a session with no rows");
    }

    [Fact]
    public async Task GetSessionSuggestions_ShouldReturnOnlyTheCallersRows_WhenTheSessionIdIsSharedAcrossTenants()
    {
        var shared = NewId("suggestions-shared");
        _factory.Records.Add(Suggestion(11, shared, Acme));
        _factory.Records.Add(Suggestion(12, shared, Globex));
        _factory.Records.Add(Suggestion(13, shared, Acme));
        _factory.Records.Add(Suggestion(14, shared, Globex));
        _factory.Records.Add(Suggestion(15, shared, Acme));
        using var acme = _factory.SupervisorClient(Acme);

        var rows = await GetRowsAsync(acme, $"/api/v1/agent-assist/sessions/{shared}/suggestions");

        rows.Select(r => r!["id"]!.GetValue<long>()).Should().Equal([11L, 13L, 15L], "only acme's rows, in the store's order");
        rows.Select(r => r!["tenantId"]!.GetValue<string>()).Should().AllBe(Acme);
    }

    // ═══ Task 1.3: allowed callers, green before and after the fix ══════════════

    [Fact]
    public async Task GetActiveSessions_ShouldListTheSession_WhenItsTenantMatchesTheCaller()
    {
        var id = NewId("own-listed");
        var call = Call($"{id}.call", Acme);
        _factory.Live.Add(AgentAssistLiveSessions.ToLive(id, call, Acme));
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = new TenantId(Acme),
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.Voice,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            VoiceLinkedId = call.LinkedId,
        };
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        }

        using var acme = _factory.SupervisorClient(Acme);

        var item = (await ListAsync(acme)).Single(i => i!["sessionId"]!.GetValue<string>() == id)!.AsObject();

        var expected = new JsonObject
        {
            ["sessionId"] = id,
            ["agentId"] = "0651f84b6f284a1c9b8d658330fa8310",
            ["queueName"] = "support",
            ["callerIdNum"] = call.CallerIdNum,
            ["connectedAt"] = "2026-11-06T15:01:00+00:00",
            ["conversationId"] = conversation.ConversationId.Value,
        };
        JsonNode.DeepEquals(item, expected).Should().BeTrue(
            $"the item keeps its shape field for field, its conversation resolved in the caller's tenant; it was {item.ToJsonString()}");
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer200_WhenTheSessionBelongsToTheCallersTenant()
    {
        var acmeSession = AddSession(Acme, "whisper-own");
        using var acme = _factory.SupervisorClient(Acme);

        using var response = await acme.PostAsJsonAsync(WhisperRoute(acmeSession), new { text = "offer the refund" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: await response.Content.ReadAsStringAsync());
        _factory.Live.Whispers.Should().ContainSingle(w => w.SessionId == acmeSession)
            .Which.Text.Should().Be("offer the refund");
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer400_WhenWhisperDeliveryIsDisabled()
    {
        var acmeSession = AddSession(Acme, "whisper-disabled", deliversWhispers: false);
        using var acme = _factory.SupervisorClient(Acme);

        using var response = await acme.PostAsJsonAsync(WhisperRoute(acmeSession), new { text = "offer the refund" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await response.Content.ReadAsStringAsync());
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!.GetValue<string>()
            .Should().Be("Whisper delivery is not enabled for this session.");
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer404_WhenTheIdNamesNoLiveSession()
    {
        var unknown = NewId("whisper-unknown");
        using var acme = _factory.SupervisorClient(Acme);

        using var response = await acme.PostAsJsonAsync(WhisperRoute(unknown), new { text = "offer the refund" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetSessionCompliance_ShouldReturnTheCallersRows_WhenTheSessionIsItsOwn()
    {
        var acmeSession = NewId("compliance-own");
        _factory.Records.Add(Alert(21, acmeSession, Acme));
        _factory.Records.Add(Alert(22, acmeSession, Acme));
        using var acme = _factory.SupervisorClient(Acme);

        var rows = await GetRowsAsync(acme, $"/api/v1/agent-assist/sessions/{acmeSession}/compliance");

        rows.Select(r => r!["id"]!.GetValue<long>()).Should().Equal([21L, 22L]);
        var first = rows[0]!.AsObject();
        first["sessionId"]!.GetValue<string>().Should().Be(acmeSession);
        first["tenantId"]!.GetValue<string>().Should().Be(Acme);
        first["ruleId"]!.GetValue<string>().Should().Be("pci-card-number");
        first["severity"]!.GetValue<string>().Should().Be("High");
    }
}
