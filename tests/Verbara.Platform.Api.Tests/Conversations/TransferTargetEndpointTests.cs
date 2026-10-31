using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Tests.SupervisorListen;
using Verbara.Platform.Conversations;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// agent-console-contract-debt (C5, C6): the pickers behind the transfer dialogs, over the real pipeline, with the
/// real role templates, the real in-memory stores and the real account statuses. Each body is compared with the
/// frozen fixture, because Platform.Web generates its types from these shapes.
/// </summary>
public sealed class TransferTargetEndpointTests : IClassFixture<TransferTargetApiFactory>
{
    private const string QueuesRoute = "/api/v1/transfer-targets/queues";
    private const string AgentsRoute = "/api/v1/transfer-targets/agents";

    private readonly TransferTargetApiFactory _factory;

    public TransferTargetEndpointTests(TransferTargetApiFactory factory)
    {
        _factory = factory;
        _factory.ClearTargets();
    }

    public static TheoryData<string> Routes => new() { QueuesRoute, AgentsRoute };

    private static JsonObject Fixture(string name)
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", name);
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    /// <summary>A client of a new Agent, whose own profile the agent list leaves out.</summary>
    private HttpClient AgentClient()
    {
        var (user, _) = _factory.NewAgent("reader");
        return _factory.ClientFor(user);
    }

    private void SeedTargets(string route, params string[] names)
    {
        foreach (var name in names)
        {
            if (route == QueuesRoute)
                _factory.SeedQueue(name);
            else
                _factory.SeedAgent(name);
        }
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpClient client, string uri)
    {
        var response = await client.GetAsync(uri);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {raw}");
        return JsonNode.Parse(raw)!.AsObject();
    }

    private static string?[] ItemsOf(JsonObject body, string member) =>
        body["items"]!.AsArray().Select(item => item![member]!.GetValue<string>()).ToArray<string?>();

    private static JsonObject ItemOf(JsonObject body, string id) =>
        body["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == id)!.AsObject();

    private static int IntOf(JsonObject body, string member) => body[member]!.GetValue<int>();

    private static bool BoolOf(JsonObject body, string member) => body[member]!.GetValue<bool>();

    // ─── "The transfer-target routes SHALL be gated by the transfer permission" ───

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TransferTargets_ShouldReturn200_WhenTheCallerIsAnAgent(string route)
    {
        using var client = AgentClient();

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TransferTargets_ShouldReturnABare403_WhenTheCallerLacksThePermission(string route)
    {
        // The Api role template does not grant contacts:conversation:transfer.
        using var client = _factory.ClientFor(_factory.NewUser(UserRole.Api, "integration"));

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TransferTargets_ShouldReturn401_WhenThereIsNoCredential(string route)
    {
        // The tenant is named, so this 401 is the policy's and not the operational gate's own missing-tenant answer.
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", ConversationOwnershipApiFactory.CustomerTenantId);

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── "Both routes SHALL share one query and one paged envelope" ───────────

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TransferTargets_ShouldApplyTheDefaults_WhenTheCallerSendsNoQuery(string route)
    {
        SeedTargets(route, "One", "Two", "Three");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, route);

        IntOf(body, "page").Should().Be(1);
        IntOf(body, "pageSize").Should().Be(100);
        IntOf(body, "totalCount").Should().Be(3);
        IntOf(body, "totalPages").Should().Be(1);
        BoolOf(body, "hasNextPage").Should().BeFalse();
        BoolOf(body, "hasPreviousPage").Should().BeFalse();
        body["items"]!.AsArray().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(QueuesRoute, "pageSize=5000", 200)]
    [InlineData(AgentsRoute, "pageSize=5000", 200)]
    [InlineData(QueuesRoute, "pageSize=201", 200)]
    [InlineData(QueuesRoute, "pageSize=200", 200)]
    [InlineData(QueuesRoute, "pageSize=1", 1)]
    [InlineData(QueuesRoute, "pageSize=0", 1)]
    [InlineData(AgentsRoute, "pageSize=0", 1)]
    [InlineData(QueuesRoute, "pageSize=-7", 1)]
    public async Task TransferTargets_ShouldClampThePageSize_WhenItIsOutOfRange(string route, string query, int expected)
    {
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, $"{route}?{query}");

        IntOf(body, "pageSize").Should().Be(expected);
    }

    [Theory]
    [InlineData(QueuesRoute, "page=0")]
    [InlineData(AgentsRoute, "page=0")]
    [InlineData(QueuesRoute, "page=-3")]
    public async Task TransferTargets_ShouldReadThePageAsOne_WhenItIsBelowOne(string route, string query)
    {
        SeedTargets(route, "One");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, $"{route}?{query}");

        IntOf(body, "page").Should().Be(1);
        body["items"]!.AsArray().Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TransferTargets_ShouldAnswerAnEmptyEnvelope_WhenTheSearchMatchesNothing(string route)
    {
        SeedTargets(route, "One");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, $"{route}?search=nothing-has-this-name");

        body["items"]!.AsArray().Should().BeEmpty();
        IntOf(body, "totalCount").Should().Be(0);
        IntOf(body, "totalPages").Should().Be(0);
        BoolOf(body, "hasNextPage").Should().BeFalse();
        BoolOf(body, "hasPreviousPage").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TransferTargets_ShouldAnswerTheAskedPage_WhenTheTotalSpansPages(string route)
    {
        SeedTargets(route, "One", "Two", "Three");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, $"{route}?page=2&pageSize=2");

        body["items"]!.AsArray().Should().ContainSingle();
        IntOf(body, "totalCount").Should().Be(3);
        IntOf(body, "totalPages").Should().Be(2);
        BoolOf(body, "hasNextPage").Should().BeFalse();
        BoolOf(body, "hasPreviousPage").Should().BeTrue();
    }

    [Theory]
    [InlineData(QueuesRoute, 3)]
    [InlineData(AgentsRoute, 3)]
    [InlineData(QueuesRoute, int.MaxValue)]
    [InlineData(AgentsRoute, int.MaxValue)]
    public async Task TransferTargets_ShouldAnswerNoItems_WhenThePageIsPastTheEnd(string route, int page)
    {
        SeedTargets(route, "One", "Two", "Three");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, $"{route}?page={page}&pageSize=200");

        body["items"]!.AsArray().Should().BeEmpty("a page past the end never wraps back to the first one");
        IntOf(body, "page").Should().Be(page);
        IntOf(body, "totalCount").Should().Be(3);
    }

    // ─── "The queue list SHALL expose only the active queues, by id and name" ───

    [Fact]
    public async Task ListTransferTargetQueues_ShouldListOnlyActiveQueues_WhenOneIsInactive()
    {
        _factory.SeedQueue("Billing");
        _factory.SeedQueue("Retired", isActive: false);
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, QueuesRoute);

        ItemsOf(body, "name").Should().Equal("Billing");
        IntOf(body, "totalCount").Should().Be(1);
    }

    [Fact]
    public async Task ListTransferTargetQueues_ShouldMatchAnyPartOfTheNameInNameOrder_WhenASearchIsSent()
    {
        _factory.SeedQueue("Billing - Escalations");
        _factory.SeedQueue("Support");
        _factory.SeedQueue("Billing");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, $"{QueuesRoute}?search=bill");

        ItemsOf(body, "name").Should().Equal("Billing", "Billing - Escalations");
        IntOf(body, "totalCount").Should().Be(2);

        var middle = await ReadBodyAsync(client, $"{QueuesRoute}?search=ESCAL");
        ItemsOf(middle, "name").Should().Equal("Billing - Escalations");
    }

    [Fact]
    public async Task ListTransferTargetQueues_ShouldCountOnlyActiveMatches_WhenAnInactiveQueueMatchesTheSearch()
    {
        _factory.SeedQueue("Billing");
        _factory.SeedQueue("Billing retired", isActive: false);
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, $"{QueuesRoute}?search=bill");

        IntOf(body, "totalCount").Should().Be(1);
    }

    [Fact]
    public async Task ListTransferTargetQueues_ShouldListAQueue_WhenTheAgentBelongsToNoQueue()
    {
        var queue = _factory.SeedQueue("Anywhere");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, QueuesRoute);

        ItemsOf(body, "id").Should().Equal(queue.QueueId.Value);
    }

    [Fact]
    public async Task ListTransferTargetQueues_ShouldOrderByNameIgnoringCase_WhenNamesDifferInCase()
    {
        _factory.SeedQueue("gamma");
        _factory.SeedQueue("Beta");
        _factory.SeedQueue("alpha");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, QueuesRoute);

        ItemsOf(body, "name").Should().Equal("alpha", "Beta", "gamma");
    }

    [Fact]
    public async Task ListTransferTargetQueues_ShouldBreakATieOnTheId_WhenTwoQueuesShareAName()
    {
        // Queue names are not unique. The stores read in id order and LINQ sorts stably, so this holds with no tie-break
        // today; the explicit one keeps pages stable if the read order changes, and only a reversed one goes red here.
        _factory.SeedQueue("Same", id: "queue-b");
        _factory.SeedQueue("Same", id: "queue-a");
        using var client = AgentClient();

        var first = await ReadBodyAsync(client, $"{QueuesRoute}?page=1&pageSize=1");
        var second = await ReadBodyAsync(client, $"{QueuesRoute}?page=2&pageSize=1");

        ItemsOf(first, "id").Should().Equal("queue-a");
        ItemsOf(second, "id").Should().Equal("queue-b");
    }

    [Fact]
    public async Task ListTransferTargetQueues_ShouldReadToTheEnd_WhenTheTenantIsOverOneInternalPage()
    {
        // One more than a page of QueueStoreExtensions.ListAllAsync. The store walks by id, so the last id read is the
        // one a single internal page would lose, and its name sorts it first: it is sorted after reading, not before.
        const int count = QueueStoreExtensions.EnumerationPageSize + 1;
        for (var i = 0; i < count - 1; i++)
            _factory.SeedQueue($"Queue {i:D4}", id: $"queue-{i:D4}");
        _factory.SeedQueue("AAA past the first internal page", id: "queue-9999");
        using var client = AgentClient();

        var first = await ReadBodyAsync(client, $"{QueuesRoute}?pageSize=200");
        var last = await ReadBodyAsync(client, $"{QueuesRoute}?page=6&pageSize=200");

        IntOf(first, "totalCount").Should().Be(count);
        IntOf(first, "totalPages").Should().Be(6);
        ItemsOf(first, "id")[0].Should().Be("queue-9999");
        ItemsOf(last, "id").Should().Equal("queue-0999");
        BoolOf(last, "hasNextPage").Should().BeFalse();
    }

    [Theory]
    [InlineData("body")]
    [InlineData("emptyBody")]
    public async Task ListTransferTargetQueues_ShouldAnswerTheFixturesBody_WhenItsQueryIsSent(string key)
    {
        var fixture = Fixture("transfer-target-queues.v1.json");
        var expected = fixture[key]!.AsObject();
        foreach (var item in fixture["body"]!["items"]!.AsArray())
            _factory.SeedQueue(item!["name"]!.GetValue<string>(), id: item["id"]!.GetValue<string>());
        _factory.SeedQueue("Support");
        var query = fixture["route"]!["query"]!;
        var search = key == "body" ? query["search"]!.GetValue<string>() : "nothing-has-this-name";
        using var client = AgentClient();

        var body = await ReadBodyAsync(
            client,
            $"{QueuesRoute}?search={search}&page={query["page"]}&pageSize={query["pageSize"]}");

        SupervisorListenTestKit.AssertSame(expected, body);
    }

    // ─── "The agent list SHALL expose only what the dialogs need" ─────────────

    [Fact]
    public async Task ListTransferTargetAgents_ShouldListAnIdTheTransferTakes_WhenTheOwnerPicksIt()
    {
        var (user, owner) = _factory.NewAgent("owner");
        var target = _factory.SeedAgent("Target");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(user);

        var listed = ItemsOf(await ReadBodyAsync(client, AgentsRoute), "id").Should().ContainSingle().Subject;
        var response = await client.PostAsJsonAsync(
            ConversationOwnershipEndpointTests.Route(conversation, "transfer"), new { targetAgentId = listed });

        listed.Should().Be(target.AgentId.Value).And.NotBe(target.UserId.Value);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(target.AgentId));
    }

    [Theory]
    [InlineData(AgentState.Offline, "Offline", false)]
    [InlineData(AgentState.Available, "Available", true)]
    [InlineData(AgentState.Busy, "Busy", true)]
    [InlineData(AgentState.Break, "Break", false)]
    [InlineData(AgentState.Lunch, "Lunch", false)]
    [InlineData(AgentState.Training, "Training", false)]
    [InlineData(AgentState.ACW, "ACW", false)]
    [InlineData(AgentState.DND, "DND", false)]
    public async Task ListTransferTargetAgents_ShouldWriteTheStateAndWhetherItIsRoutable_WhenAnAgentIsInIt(
        AgentState state, string name, bool canAcceptWork)
    {
        var agent = _factory.SeedAgent("Target", state);
        using var client = AgentClient();

        var item = ItemOf(await ReadBodyAsync(client, AgentsRoute), agent.AgentId.Value);

        item["state"]!.GetValue<string>().Should().Be(name);
        item["canAcceptWork"]!.GetValue<bool>().Should().Be(canAcceptWork);
    }

    [Fact]
    public async Task ListTransferTargetAgents_ShouldMatchAnyPartOfTheDisplayName_WhenASearchIsSent()
    {
        _factory.SeedAgent("Hannah Osei");
        _factory.SeedAgent("Bruno Lima");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, $"{AgentsRoute}?search=OSE");

        ItemsOf(body, "displayName").Should().Equal("Hannah Osei");
        IntOf(body, "totalCount").Should().Be(1);
    }

    [Theory]
    [InlineData("body")]
    public async Task ListTransferTargetAgents_ShouldAnswerTheFixturesBody_WhenItsQueryIsSent(string key)
    {
        var fixture = Fixture("transfer-target-agents.v1.json");
        var expected = fixture[key]!.AsObject();
        foreach (var item in expected["items"]!.AsArray().Reverse())
        {
            _factory.SeedAgent(
                item!["displayName"]!.GetValue<string>(),
                Enum.Parse<AgentState>(item["state"]!.GetValue<string>()),
                id: item["id"]!.GetValue<string>());
        }

        _factory.SeedAgent("Suspended Dan", status: UserStatus.Suspended);
        _factory.SeedAgent("Bruno Lima");
        // The caller matches the search too, and is still left out.
        var caller = _factory.NewUser(UserRole.Agent, "ana-caller");
        var profile = _factory.AddAgentProfile(caller);
        profile.DisplayName = "Ana Caller";
        _factory.Save(profile);
        var query = fixture["route"]!["query"]!;
        using var client = _factory.ClientFor(caller);

        var body = await ReadBodyAsync(
            client,
            $"{AgentsRoute}?search={query["search"]!.GetValue<string>()}&page={query["page"]}&pageSize={query["pageSize"]}");

        var items = body["items"]!.AsArray();
        var want = expected["items"]!.AsArray();
        items.Should().HaveSameCount(want);
        for (var i = 0; i < want.Count; i++)
            SupervisorListenTestKit.AssertSame(want[i]!.AsObject(), items[i]!.AsObject());
        SupervisorListenTestKit.AssertSame(expected, body);
    }

    // ─── "The agent list SHALL hold only agents that can be made owners" ──────

    [Fact]
    public async Task ListTransferTargetAgents_ShouldLeaveTheCallerOut_WhenTheCallerHoldsAnAgentProfile()
    {
        var (user, agent) = _factory.NewAgent("reader");
        _factory.SeedAgent("Someone else");
        using var client = _factory.ClientFor(user);

        var body = await ReadBodyAsync(client, AgentsRoute);

        ItemsOf(body, "id").Should().NotContain(agent.AgentId.Value);
        ItemsOf(body, "displayName").Should().Equal("Someone else");
        IntOf(body, "totalCount").Should().Be(1);
    }

    [Fact]
    public async Task ListTransferTargetAgents_ShouldListEveryAgent_WhenTheCallerHoldsNoAgentProfile()
    {
        _factory.SeedAgent("Someone");
        using var client = _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, "no-profile"));

        var body = await ReadBodyAsync(client, AgentsRoute);

        ItemsOf(body, "displayName").Should().Equal("Someone");
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task ListTransferTargetAgents_ShouldNotCountTheAgent_WhenItsUserIsNotActive(UserStatus status)
    {
        var excluded = _factory.SeedAgent("Not active", status: status);
        _factory.SeedAgent("Active");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, AgentsRoute);

        ItemsOf(body, "id").Should().NotContain(excluded.AgentId.Value);
        IntOf(body, "totalCount").Should().Be(1);
    }

    [Fact]
    public async Task ListTransferTargetAgents_ShouldNotListTheAgent_WhenItsUserIsMissing()
    {
        _factory.SeedAgent("No user", hasUser: false);
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, AgentsRoute);

        body["items"]!.AsArray().Should().BeEmpty();
        IntOf(body, "totalCount").Should().Be(0);
    }

    [Fact]
    public async Task ListTransferTargetAgents_ShouldListTheAgent_WhenItsUserIsActiveAndItIsOffline()
    {
        var agent = _factory.SeedAgent("Off shift", AgentState.Offline);
        using var client = AgentClient();

        var item = ItemOf(await ReadBodyAsync(client, AgentsRoute), agent.AgentId.Value);

        item["state"]!.GetValue<string>().Should().Be("Offline");
        item["canAcceptWork"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ListTransferTargetAgents_ShouldLookUpTheAccountStatusOnce_WhenTheTenantHasFiftyAgents()
    {
        var seeded = Enumerable.Range(0, 50).Select(i => _factory.SeedAgent($"Agent {i:D2}")).ToList();
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, AgentsRoute);

        IntOf(body, "totalCount").Should().Be(50);
        var lookup = _factory.LookupsOf(seeded[0].UserId).Should().ContainSingle().Subject;
        lookup.Should().BeEquivalentTo(seeded.Select(agent => agent.UserId), "one call answers for the whole set");
    }

    [Fact]
    public async Task ListTransferTargetAgents_ShouldOrderByDisplayNameIgnoringCase_WhenNamesDifferInCase()
    {
        _factory.SeedAgent("gamma");
        _factory.SeedAgent("Beta");
        _factory.SeedAgent("alpha");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, AgentsRoute);

        ItemsOf(body, "displayName").Should().Equal("alpha", "Beta", "gamma");
    }

    [Fact]
    public async Task ListTransferTargetAgents_ShouldBreakATieOnTheId_WhenTwoAgentsShareADisplayName()
    {
        _factory.SeedAgent("Same", id: "agent-b");
        _factory.SeedAgent("Same", id: "agent-a");
        using var client = AgentClient();

        var first = await ReadBodyAsync(client, $"{AgentsRoute}?page=1&pageSize=1");
        var second = await ReadBodyAsync(client, $"{AgentsRoute}?page=2&pageSize=1");

        ItemsOf(first, "id").Should().Equal("agent-a");
        ItemsOf(second, "id").Should().Equal("agent-b");
    }

    [Fact]
    public async Task ListTransferTargetAgents_ShouldReadToTheEnd_WhenTheTenantIsOverOneInternalPage()
    {
        // ListAllAsync's page is 1000. These agents have no user, so only the last one seeded is listed, and it is the
        // one a single internal page would lose. Skipping the users keeps the seed to what the walk reads.
        for (var i = 0; i < 1000; i++)
            _factory.SeedAgent($"Agent {i:D4}", id: $"agent-{i:D4}", hasUser: false);
        var last = _factory.SeedAgent("Last", id: "agent-9999");
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, AgentsRoute);

        ItemsOf(body, "id").Should().Equal(last.AgentId.Value);
    }

    // ─── "The routes SHALL NOT expose admin data" ─────────────────────────────

    [Fact]
    public async Task ListTransferTargetQueues_ShouldHoldExactlyTheFixturesKeys_WhenTheQueueCarriesAdminData()
    {
        _factory.SeedQueue("Billing");
        var fixture = Fixture("transfer-target-queues.v1.json")["body"]!.AsObject();
        using var client = AgentClient();

        var body = await ReadBodyAsync(client, QueuesRoute);

        body.Select(member => member.Key).Should().Equal(fixture.Select(member => member.Key));
        body["items"]![0]!.AsObject().Select(member => member.Key).Should()
            .Equal(fixture["items"]![0]!.AsObject().Select(member => member.Key));
    }

    [Fact]
    public async Task ListTransferTargetAgents_ShouldHoldExactlyTheFixturesKeys_WhenTheAgentCarriesAdminData()
    {
        var agent = _factory.SeedAgent("Target");
        agent.Extension = "7001";
        agent.PendingState = AgentState.Break;
        agent.PendingReason = "pending-reason-marker";
        agent.Skills = ["skill-marker"];
        _factory.Save(agent);
        var fixture = Fixture("transfer-target-agents.v1.json")["body"]!.AsObject();
        using var client = AgentClient();

        var response = await client.GetAsync(AgentsRoute);
        var raw = await response.Content.ReadAsStringAsync();

        var body = JsonNode.Parse(raw)!.AsObject();
        body.Select(member => member.Key).Should().Equal(fixture.Select(member => member.Key));
        body["items"]![0]!.AsObject().Select(member => member.Key).Should()
            .Equal(fixture["items"]![0]!.AsObject().Select(member => member.Key));
        raw.Should().NotContain(agent.UserId.Value)
            .And.NotContain("7001")
            .And.NotContain("pending-reason-marker")
            .And.NotContain("skill-marker");
    }

    [Theory]
    [InlineData("/api/v1/admin/queues")]
    [InlineData("/api/v1/admin/agents")]
    public async Task AdminRoutes_ShouldStayClosedToAnAgent_WhenTheTransferTargetsAreOpen(string route)
    {
        using var client = AgentClient();

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
