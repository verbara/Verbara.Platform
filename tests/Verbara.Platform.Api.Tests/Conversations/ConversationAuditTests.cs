using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// The audit entry a conversation change writes: actor = the calling user, the impersonation context when
/// impersonating, and a failed write logged instead of failing a change that has already been made.
/// </summary>
public sealed class ConversationAuditTests
{
    private static readonly TenantId s_tenant = new("tenant-1");
    private static readonly EntityId s_conversation = EntityId.From("conv-1");

    [Fact]
    public async Task TryRecordAsync_ShouldRecordTheCallerAndTheImpersonationContext_WhenTheCallerImpersonates()
    {
        var audit = Substitute.For<IAuditService>();
        var context = ContextFor(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "admin-7"),
            new Claim("impersonation", "true"),
            new Claim("impersonator_id", "admin-7"),
            new Claim("impersonator_tenant", "platform"),
            new Claim("impersonation_session_id", "session-9"),
        ], "test")));

        await ConversationAudit.TryRecordAsync(
            context, audit, s_tenant, "conversation.transferred", s_conversation,
            new Dictionary<string, string> { ["target_queue"] = "queue-1" }, CancellationToken.None);

        await audit.Received(1).RecordAsync(
            s_tenant,
            "conversations",
            "conversation.transferred",
            "info",
            "admin-7",
            "user",
            s_conversation.Value,
            "Conversation",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(m =>
                m != null
                && m["target_queue"] == "queue-1"
                && m["impersonator_tenant"] == "platform"
                && m["impersonation_session_id"] == "session-9"),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRecordAsync_ShouldLogAndReturn_WhenTheAuditWriteFails()
    {
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(default, default!, default!, default!, default!, default!)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("audit store unavailable")));
        var capture = new LogRecordCapture();
        var context = ContextFor(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "agent-user-1")], "test")), capture);

        var record = () => ConversationAudit.TryRecordAsync(
            context, audit, s_tenant, "conversation.taken_over", s_conversation,
            new Dictionary<string, string>(), CancellationToken.None);

        await record.Should().NotThrowAsync(because: "the change was made before its entry was written");
        capture.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.EventId.Id == 7510)
            .Which.Message.Should().Contain("conversation.taken_over").And.Contain(s_conversation.Value);
    }

    private static DefaultHttpContext ContextFor(ClaimsPrincipal user, LogRecordCapture? capture = null)
    {
        var services = new ServiceCollection().AddLogging(logging =>
        {
            if (capture is not null)
                logging.AddProvider(capture);
        });
        return new DefaultHttpContext { User = user, RequestServices = services.BuildServiceProvider() };
    }
}

/// <summary>
/// agent-console-contract-debt (C3): the <c>conversation.transferred</c> entry says what happened to a note and never
/// holds its text, which may carry customer data. Every other key stays as it was.
/// </summary>
public sealed class TransferNoteAuditTests : IClassFixture<TransferNoteApiFactory>
{
    private const string Note = "Customer disputes the October invoice; please do not re-issue it.";

    private readonly TransferNoteApiFactory _factory;

    public TransferNoteAuditTests(TransferNoteApiFactory factory) => _factory = factory;

    private (User User, Agent Agent, Conversation Conversation) SeedOwned()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
        return (user, agent, conversation);
    }

    private AuditEntry TransferEntryOf(Conversation conversation) =>
        _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(entry => entry.Action == "conversation.transferred").Subject;

    private static void ShouldHoldNoText(AuditEntry entry)
    {
        entry.Changes.Should().BeNull();
        string?[] fields =
        [
            entry.Action, entry.Category, entry.Severity, entry.ActorId, entry.ActorType, entry.TargetId, entry.TargetType,
            entry.IntegrityHash, entry.ImpersonatorId, .. entry.Metadata!.Keys, .. entry.Metadata.Values,
        ];
        fields.Should().NotContain(field => field != null && field.Contains(Note, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transfer_ShouldAuditNoteNone_WhenThereIsNoNote()
    {
        var (user, agent, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            ConversationOwnershipEndpointTests.Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var metadata = TransferEntryOf(conversation).Metadata!;
        metadata.Keys.Should().BeEquivalentTo("from_owner_kind", "from_owner_id", "target_queue", "note");
        metadata["from_owner_kind"].Should().Be("Agent");
        metadata["from_owner_id"].Should().Be(agent.AgentId.Value);
        metadata["target_queue"].Should().Be(queue.QueueId.Value);
        metadata["note"].Should().Be("none");
    }

    [Fact]
    public async Task Transfer_ShouldAuditNoteNone_WhenTheNoteIsBlank()
    {
        var (user, _, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            ConversationOwnershipEndpointTests.Route(conversation, "transfer"),
            new { targetQueueId = queue.QueueId.Value, note = " \n " });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        TransferEntryOf(conversation).Metadata!["note"].Should().Be("none");
    }

    [Fact]
    public async Task Transfer_ShouldAuditNoteWrittenAndNoText_WhenTheNoteIsStored()
    {
        var (user, agent, conversation) = SeedOwned();
        var (_, target) = _factory.NewAgent("target");
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            ConversationOwnershipEndpointTests.Route(conversation, "transfer"),
            new { targetAgentId = target.AgentId.Value, note = Note });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = TransferEntryOf(conversation);
        entry.Metadata!.Keys.Should().BeEquivalentTo("from_owner_kind", "from_owner_id", "target_agent", "note");
        entry.Metadata["from_owner_id"].Should().Be(agent.AgentId.Value);
        entry.Metadata["target_agent"].Should().Be(target.AgentId.Value);
        entry.Metadata["note"].Should().Be("written");
        ShouldHoldNoText(entry);
    }

    [Fact]
    public async Task Transfer_ShouldAuditNoteWrittenBesideBySupervisor_WhenASupervisorLeavesTheNote()
    {
        var (_, _, conversation) = SeedOwned();
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsJsonAsync(
            ConversationOwnershipEndpointTests.Route(conversation, "transfer"),
            new { targetQueueId = queue.QueueId.Value, note = Note });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = TransferEntryOf(conversation);
        entry.Metadata!.Keys.Should().BeEquivalentTo(
            "from_owner_kind", "from_owner_id", "target_queue", "by_supervisor", "note");
        entry.Metadata["by_supervisor"].Should().Be(supervisor.UserId.Value);
        entry.Metadata["note"].Should().Be("written");
        ShouldHoldNoText(entry);
    }

    [Fact]
    public async Task Transfer_ShouldAuditNoteFailedAndNoText_WhenTheNoteWriteFails()
    {
        var (user, _, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        _factory.RefuseWritesTo(conversation.ConversationId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            ConversationOwnershipEndpointTests.Route(conversation, "transfer"),
            new { targetQueueId = queue.QueueId.Value, note = Note });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = TransferEntryOf(conversation);
        entry.Metadata!.Keys.Should().BeEquivalentTo("from_owner_kind", "from_owner_id", "target_queue", "note");
        entry.Metadata["note"].Should().Be("failed");
        ShouldHoldNoText(entry);
    }
}
