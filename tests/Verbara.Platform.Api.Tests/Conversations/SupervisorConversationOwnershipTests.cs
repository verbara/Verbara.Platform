using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// The supervisor routes that move a conversation, over the real pipeline: a takeover makes the
/// supervisor's own Agent profile the owner (a supervisor without one is refused), is audited and
/// announced once; a reassign refuses a target that does not exist before it changes anything.
/// </summary>
public sealed class SupervisorConversationOwnershipTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private readonly ConversationOwnershipApiFactory _factory;

    public SupervisorConversationOwnershipTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    // ─── takeover ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Takeover_ShouldOwnBySupervisorsAgentIdAndWriteAudit_WhenSupervisorHasAnAgentProfile()
    {
        var (supervisor, supervisorAgent) = _factory.NewAgent("supervisor", UserRole.Supervisor);
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        _factory.Reserve(owner.AgentId);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsync(Takeover(conversation), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(supervisorAgent.AgentId));
        _factory.LoadOf(owner.AgentId).Should().Be(0);
        _factory.LoadOf(supervisorAgent.AgentId).Should().Be(1);

        var entry = _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(e => e.Action == "conversation.taken_over").Subject;
        entry.ActorId.Should().Be(supervisor.UserId.Value);
        entry.Metadata!["agent_id"].Should().Be(supervisorAgent.AgentId.Value);
        entry.Metadata["previous_owner_id"].Should().Be(owner.AgentId.Value);
        entry.Metadata["previous_owner_kind"].Should().Be(nameof(ConversationOwnerKind.Agent));
    }

    [Fact]
    public async Task Takeover_ShouldPublishOneAssignedEvent_WhenItSucceeds()
    {
        var (supervisor, supervisorAgent) = _factory.NewAgent("supervisor", UserRole.Supervisor);
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(supervisor);
        var assigned = new List<Verbara.Platform.Core.ConversationAssignedEvent>();
        using var subscription = _factory.Services.GetRequiredService<PlatformEventBus>().Events.Subscribe(e =>
        {
            if (e is Verbara.Platform.Core.ConversationAssignedEvent a && a.ConversationId == conversation.ConversationId.Value)
                lock (assigned) assigned.Add(a);
        });

        var response = await client.PostAsync(Takeover(conversation), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        lock (assigned)
            assigned.Should().ContainSingle().Which.AgentId.Should().Be(supervisorAgent.AgentId.Value);
    }

    [Fact]
    public async Task Takeover_ShouldReturn403AndKeepOwner_WhenSupervisorHasNoAgentProfile()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "no-profile");
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsync(Takeover(conversation), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-an-agent");
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
        _factory.AuditOf(conversation.ConversationId).Should().BeEmpty();
    }

    [Fact]
    public async Task Takeover_ShouldReturn403AndKeepOwner_WhenCallerIsAnAgent()
    {
        var (agent, _) = _factory.NewAgent("agent");
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(agent);

        var response = await client.PostAsync(Takeover(conversation), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
    }

    [Fact]
    public async Task Takeover_ShouldReturn404_WhenConversationDoesNotExist()
    {
        var (supervisor, _) = _factory.NewAgent("supervisor", UserRole.Supervisor);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsync("/api/v1/supervisor/conversations/no-such-conversation/takeover", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Takeover_ShouldReturn409AndReleaseNothing_WhenTheConversationCannotMove()
    {
        var (supervisor, supervisorAgent) = _factory.NewAgent("supervisor", UserRole.Supervisor);
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Closed);
        _factory.Reserve(owner.AgentId);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsync(Takeover(conversation), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
        _factory.LoadOf(owner.AgentId).Should().Be(1);
        _factory.LoadOf(supervisorAgent.AgentId).Should().Be(0);
    }

    // ─── reassign ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reassign_ShouldReturn400AndChangeNothing_WhenTargetAgentDoesNotExist()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active, FailoverMarkers());
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsJsonAsync(Reassign(conversation), new { targetAgentId = "ghost-agent" });

        await ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, "target-agent-not-found");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
        stored.Metadata.Should().ContainKey("failoverStuck").And.ContainKey("failoverAttempts");
    }

    [Fact]
    public async Task Reassign_ShouldReturn400AndChangeNothing_WhenTargetQueueDoesNotExist()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active, FailoverMarkers());
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsJsonAsync(Reassign(conversation), new { targetQueueId = "ghost-queue" });

        await ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, "target-queue-not-found");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
        stored.State.Should().Be(ConversationState.Active);
        stored.Metadata.Should().ContainKey("failoverStuck").And.ContainKey("failoverAttempts");
    }

    [Fact]
    public async Task Reassign_ShouldMoveTheConversation_WhenTargetAgentExists()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var (_, owner) = _factory.NewAgent("owner");
        var (_, target) = _factory.NewAgent("target");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active, FailoverMarkers());
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsJsonAsync(Reassign(conversation), new { targetAgentId = target.AgentId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.Owner.Should().Be(ConversationOwner.ForAgent(target.AgentId));
        stored.Metadata.Should().NotContainKey("failoverStuck");
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, string> FailoverMarkers() => new()
    {
        ["failoverAttempts"] = "3",
        ["failoverStuck"] = "true",
    };

    private static string Takeover(Conversation conversation) =>
        $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/takeover";

    private static string Reassign(Conversation conversation) =>
        $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/reassign";

    private static async Task ShouldBeErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, because: $"the response was {body}");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().Should().Be(error);
    }
}
