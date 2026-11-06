using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// supervisor-conversation-reads-follow-the-read-rule, the four <c>/supervisor</c> reads over the real host (design
/// D1-D3, D6; spec <c>conversation-read-scope</c>, its supervisor requirements).
/// <list type="bullet">
/// <item><b>Regression tests, written first against the unfixed code (task 1.1).</b> The four reads were gated by the
/// <c>SupervisorPlus</c> role policy only, so each answered 200 where the rule answers 403 <c>not-readable</c>, and the
/// transcript answered 200 <c>[]</c> where an unknown id answers 404. They turn green with tasks 2.1-2.2.</item>
/// <item><b>Allowed-caller tests (task 1.2)</b>, green before and after the change: the Supervisor template, an
/// impersonation session minted with monitor, a management key, a reader without listening, a Supervisor-role owner,
/// and the callers the role policy refuses as before.</item>
/// </list>
/// </summary>
/// <remarks>
/// The "Supervisor without monitor" caller keeps the Supervisor role claim (so it passes <c>SupervisorPlus</c>) and is
/// granted the <c>agent</c> template, which holds <c>contacts:conversation:handle</c> without <c>monitor</c> or
/// <c>read</c>.
/// </remarks>
public sealed class SupervisorReadScopeTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private const string NotReadable = "not-readable";
    private const string MonitorPermission = "contacts:conversation:monitor";

    private const string ListRoute = "/api/v1/supervisor/conversations";
    private const string StuckRoute = "/api/v1/supervisor/conversations/stuck";
    private const string ActiveSessionsRoute = "/api/v1/supervisor/sessions/active";

    private readonly ConversationOwnershipApiFactory _factory;

    public SupervisorReadScopeTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    private static string SupervisorMessagesRoute(Conversation conversation) =>
        SupervisorMessagesRoute(conversation.ConversationId.Value);

    private static string SupervisorMessagesRoute(string conversationId) =>
        $"/api/v1/supervisor/conversations/{conversationId}/messages?limit=50&offset=0";

    private static async Task ShouldBeNotReadableAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: $"the response was {body}");
        JsonNode.DeepEquals(JsonNode.Parse(body), new JsonObject { ["error"] = NotReadable })
            .Should().BeTrue(because: $"the refusal body is {{\"error\":\"{NotReadable}\"}}, and it was {body}");
    }

    private static async Task<JsonNode> ShouldBeOkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {body}");
        return JsonNode.Parse(body)!;
    }

    /// <summary>A Supervisor-role user granted the agent template: handle, no monitor, no read, no Agent profile.</summary>
    private User SupervisorWithoutMonitor(string label)
    {
        var user = _factory.NewUser(UserRole.Supervisor, label);
        _factory.GrantTemplate(user, "agent");
        return user;
    }

    /// <summary>A Supervisor-role user granted the agent template, with an Agent profile that owns an Active conversation.</summary>
    private (User User, Conversation Owned) SupervisorWithoutMonitorOwningWork(string label)
    {
        var (user, agent) = _factory.NewAgent(label, UserRole.Supervisor);
        _factory.GrantTemplate(user, "agent");
        var owned = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
        return (user, owned);
    }

    /// <summary>An Active conversation owned by a fresh agent A.</summary>
    private Conversation ConversationOfAgentA()
    {
        var (_, agentA) = _factory.NewAgent("owner-a");
        return _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active);
    }

    private void SeedMessage(Conversation conversation, string text, DateTimeOffset createdAt)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMessageStore>().SaveAsync(new Message
        {
            MessageId = EntityId.From($"m-{Guid.NewGuid():N}"),
            ConversationId = conversation.ConversationId,
            TenantId = conversation.TenantId,
            Direction = MessageDirection.Inbound,
            Channel = conversation.Channel,
            SenderId = "customer",
            Content = new MessageEnvelope([new TextBlock(text)]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = createdAt,
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>An offline agent holding one Active conversation: what <c>/conversations/stuck</c> lists.</summary>
    private Conversation SeedStuckConversation()
    {
        var (_, agent) = _factory.NewAgent("offline-owner");
        agent.State = AgentState.Offline;
        agent.OfflineSince = DateTimeOffset.UtcNow.AddMinutes(-10);
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IAgentStore>().SaveAsync(agent, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        return _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
    }

    private Conversation SeedForeignConversation()
    {
        var foreign = new Conversation
        {
            ConversationId = EntityId.From($"conv-foreign-{Guid.NewGuid():N}"),
            TenantId = new TenantId(ConversationOwnershipApiFactory.PlatformTenantId),
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _factory.Services.GetRequiredService<IConversationStore>().SaveAsync(foreign, CancellationToken.None)
            .GetAwaiter().GetResult();
        return foreign;
    }

    // ═══ Task 1.1: regression tests, red on the unfixed code ═══════════════════

    [Fact]
    public async Task GetSupervisorMessages_ShouldRefuseNotReadable_WhenTheSupervisorHoldsNeitherMonitorNorRead()
    {
        var conversation = ConversationOfAgentA();
        SeedMessage(conversation, "the customer's account details", DateTimeOffset.UtcNow);
        using var client = _factory.ClientFor(SupervisorWithoutMonitor("no-monitor-transcript"));

        await ShouldBeNotReadableAsync(await client.GetAsync(SupervisorMessagesRoute(conversation)));
    }

    [Fact]
    public async Task ListSupervisorConversations_ShouldRefuseNotReadable_WhenTheSupervisorHoldsNeitherMonitorNorRead()
    {
        ConversationOfAgentA();
        var (user, _) = SupervisorWithoutMonitorOwningWork("no-monitor-list");
        using var client = _factory.ClientFor(user);

        await ShouldBeNotReadableAsync(await client.GetAsync(ListRoute));
    }

    [Fact]
    public async Task GetStuckConversations_ShouldRefuseNotReadable_WhenTheSupervisorHoldsNeitherMonitorNorRead()
    {
        SeedStuckConversation();
        var (user, _) = SupervisorWithoutMonitorOwningWork("no-monitor-stuck");
        using var client = _factory.ClientFor(user);

        await ShouldBeNotReadableAsync(await client.GetAsync(StuckRoute));
    }

    [Fact]
    public async Task GetActiveSessions_ShouldRefuseNotReadable_WhenTheSupervisorHoldsNeitherMonitorNorRead()
    {
        var (user, _) = SupervisorWithoutMonitorOwningWork("no-monitor-sessions");
        using var client = _factory.ClientFor(user);

        await ShouldBeNotReadableAsync(await client.GetAsync(ActiveSessionsRoute));
    }

    [Fact]
    public async Task SupervisorReads_ShouldRefuseNotReadable_WhenTheImpersonationSessionCarriesNoMonitorOrRead()
    {
        var conversation = ConversationOfAgentA();
        var admin = _factory.NewPlatformAdmin();
        using var client = _factory.CreateBearerClient(
            _factory.MintImpersonationToken(admin, ["contacts:contact:view"], readOnly: false));

        await ShouldBeNotReadableAsync(await client.GetAsync(SupervisorMessagesRoute(conversation)));
        await ShouldBeNotReadableAsync(await client.GetAsync(ListRoute));
        await ShouldBeNotReadableAsync(await client.GetAsync(StuckRoute));
        await ShouldBeNotReadableAsync(await client.GetAsync(ActiveSessionsRoute));
    }

    [Fact]
    public async Task GetActiveSessions_ShouldRefuseNotReadable_WhenTheApiKeyIsBoundToASupervisorWithoutMonitor()
    {
        using var client = _factory.ApiKeyClientBoundTo(SupervisorWithoutMonitor("bound-key-owner"));

        await ShouldBeNotReadableAsync(await client.GetAsync(ActiveSessionsRoute));
    }

    [Fact]
    public async Task GetSupervisorMessages_ShouldAnswer404_WhenTheIdNamesNoConversationOfTheTenant()
    {
        using var supervisor = _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, "unknown-transcript"));

        var response = await supervisor.GetAsync(SupervisorMessagesRoute($"conv-unknown-{Guid.NewGuid():N}"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a missing id answers 404 before the rule runs");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("a 404 keeps its empty body");
    }

    [Fact]
    public async Task GetSupervisorMessages_ShouldAnswer404_WhenTheIdNamesAConversationOfAnotherTenant()
    {
        var foreign = SeedForeignConversation();
        using var supervisor = _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, "foreign-transcript"));

        var response = await supervisor.GetAsync(SupervisorMessagesRoute(foreign));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "another tenant's id names no conversation of this one");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("a 404 keeps its empty body");
    }

    // ═══ Task 1.2: allowed callers, green before and after the change ═════════

    [Fact]
    public async Task SupervisorReads_ShouldAnswerTheirShapes_WhenTheSupervisorHoldsTheSupervisorTemplate()
    {
        var conversation = ConversationOfAgentA();
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);
        SeedMessage(conversation, "first", first);
        SeedMessage(conversation, "second", first.AddMinutes(1));
        var stuck = SeedStuckConversation();
        using var client = _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, "template-supervisor"));

        var transcript = (await ShouldBeOkAsync(await client.GetAsync(SupervisorMessagesRoute(conversation)))).AsArray();
        transcript.Select(m => m!["content"]!["blocks"]![0]!["text"]!.GetValue<string>())
            .Should().Equal(["first", "second"], "the transcript is a bare array, oldest first");

        var page = await ShouldBeOkAsync(await client.GetAsync($"{ListRoute}?pageSize=100"));
        page["items"]!.AsArray().Select(item => item!["conversationId"]!.GetValue<string>())
            .Should().Contain(conversation.ConversationId.Value, "the list is the tenant's paged result");

        var stuckList = (await ShouldBeOkAsync(await client.GetAsync(StuckRoute))).AsArray();
        stuckList.Select(item => item!["conversationId"]!.GetValue<string>())
            .Should().Contain(stuck.ConversationId.Value, "an offline agent's Active work is stuck");

        (await ShouldBeOkAsync(await client.GetAsync(ActiveSessionsRoute))).Should().BeOfType<JsonArray>();
    }

    [Fact]
    public async Task SupervisorReads_ShouldAnswer200_WhenTheImpersonationSessionCarriesMonitor()
    {
        var conversation = ConversationOfAgentA();
        var admin = _factory.NewPlatformAdmin();
        using var client = _factory.CreateBearerClient(
            _factory.MintImpersonationToken(admin, [MonitorPermission], readOnly: false));

        await ShouldBeOkAsync(await client.GetAsync(SupervisorMessagesRoute(conversation)));
        await ShouldBeOkAsync(await client.GetAsync(ListRoute));
        await ShouldBeOkAsync(await client.GetAsync(StuckRoute));
        await ShouldBeOkAsync(await client.GetAsync(ActiveSessionsRoute));
    }

    [Fact]
    public async Task SupervisorReads_ShouldAnswer409TenantTypeMismatch_WhenTheCallerIsAManagementKey()
    {
        // As before: a management key belongs to the Platform tenant, and the API-key handler sets the request's
        // tenant to the key's own (an X-Tenant-Id header does not move it), so the group's operational-tenant filter
        // answers 409 before the role policy's admitted caller reaches the handler. The rule never runs for it.
        var conversation = ConversationOfAgentA();
        using var client = _factory.CreateBearerClient(_factory.NewManagementKey(_factory.NewPlatformAdmin()));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", ConversationOwnershipApiFactory.CustomerTenantId);

        foreach (var route in new[] { SupervisorMessagesRoute(conversation), ListRoute, StuckRoute, ActiveSessionsRoute })
        {
            var response = await client.GetAsync(route);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, because: $"{route} answered {body}");
            JsonNode.Parse(body)!["type"]!.GetValue<string>().Should().EndWith("/tenant-type-mismatch");
        }
    }

    [Fact]
    public async Task ListSupervisorConversations_ShouldAnswer200_WhenTheSupervisorHoldsReadWithoutMonitor()
    {
        var conversation = ConversationOfAgentA();
        var reader = _factory.NewUser(UserRole.Supervisor, "quality-analyst-supervisor");
        _factory.GrantTemplate(reader, "quality_analyst");
        using var client = _factory.ClientFor(reader);

        var page = await ShouldBeOkAsync(await client.GetAsync($"{ListRoute}?pageSize=100"));

        page["items"]!.AsArray().Select(item => item!["conversationId"]!.GetValue<string>())
            .Should().Contain(conversation.ConversationId.Value);
    }

    [Fact]
    public async Task SupervisorReads_ShouldAnswer403_WhenTheCallerIsAManagerOutsideTheRolePolicy()
    {
        var conversation = ConversationOfAgentA();
        var manager = _factory.NewUser(UserRole.Agent, "manager");
        _factory.GrantTemplate(manager, "manager");
        using var client = _factory.ClientFor(manager);

        // The role policy answers, not the rule: assert the status only.
        (await client.GetAsync(SupervisorMessagesRoute(conversation))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(ListRoute)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(StuckRoute)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(ActiveSessionsRoute)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SupervisorReads_ShouldAnswer403_WhenTheCallerIsATenantApiKeyBoundToNoUser()
    {
        var conversation = ConversationOfAgentA();
        using var unbound = _factory.UnboundApiKeyClient();

        // The role policy answers, not the rule: assert the status only.
        (await unbound.GetAsync(SupervisorMessagesRoute(conversation))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await unbound.GetAsync(ListRoute)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await unbound.GetAsync(StuckRoute)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await unbound.GetAsync(ActiveSessionsRoute)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetSupervisorMessages_ShouldAnswer200_WhenTheSupervisorRoleAgentOwnsTheConversation()
    {
        var (user, owned) = SupervisorWithoutMonitorOwningWork("supervisor-owner");
        SeedMessage(owned, "my own conversation", DateTimeOffset.UtcNow);
        using var client = _factory.ClientFor(user);

        var transcript = (await ShouldBeOkAsync(await client.GetAsync(SupervisorMessagesRoute(owned)))).AsArray();

        transcript.Should().ContainSingle();
    }
}
