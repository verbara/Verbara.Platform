using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>
/// contact-history-opens-by-assignment task 2.8 (design D17; spec <c>conversation-read-scope</c> "The live stream MUST
/// deliver conversation content only to its readers"): the recipients of each content event type on real streams, past
/// the publishers task 1.9 already drives. An agent reply (the send path), the readers of every conversation without an
/// Agent profile, the four <c>agentassist.*</c> keyed on the agent they name, and <c>csat.response_recorded</c>.
/// </summary>
public sealed class ConversationContentRecipientsTests : IClassFixture<ContentDeliveryApiFactory>
{
    private readonly ContentDeliveryApiFactory _factory;

    public ConversationContentRecipientsTests(ContentDeliveryApiFactory factory) => _factory = factory;

    private static string Tenant => ConversationOwnershipApiFactory.Tenant.Value;

    private Task SyncAsync(params LiveStreamProbe[] probes) => LiveStreamProbe.SyncAsync(_factory.Bus, Tenant, probes);

    private User QualityAnalyst()
    {
        var user = _factory.NewUser(UserRole.Agent, "quality-analyst");
        _factory.GrantTemplate(user, "quality_analyst");
        return user;
    }

    private static bool ReceivedFrame(LiveStreamProbe stream, string eventType, string conversationId) =>
        stream.Frames.Any(f => f.Event == eventType && f.Member("conversationId") == conversationId);

    [Fact]
    public async Task SendMessage_ShouldReachTheOwnerAndReadersOnly_WhenTheOwnerAgentReplies()
    {
        var (userA, agentA) = _factory.NewAgent("reply-owner");
        var (userO, agentO) = _factory.NewAgent("reply-offered-elsewhere");
        var (userU, _) = _factory.NewAgent("reply-unrelated");
        var supervisorUser = _factory.NewUser(UserRole.Supervisor, "reply-reader");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active);
        _factory.SeedOfferTo(agentO, _factory.SeedQueue()); // O's open offer on another conversation of the same kind
        await using var streamA = await _factory.OpenStreamAsync("owner A", userA);
        await using var streamO = await _factory.OpenStreamAsync("offered elsewhere O", userO);
        await using var streamU = await _factory.OpenStreamAsync("unrelated U", userU);
        await using var streamS = await _factory.OpenStreamAsync("supervisor", supervisorUser);
        using var capture = new MessageEventCapture(_factory.Bus);
        using var clientA = _factory.ClientFor(userA);

        var response = await clientA.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation.ConversationId.Value}/messages", new { text = "how can I help?" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var reply = await capture.WaitForAsync(
            e => e.ConversationId == conversation.ConversationId.Value && e.Sender == "Outbound", "the agent's reply");
        reply.Audience.Should().Be(new ConversationAudience(agentA.AgentId.Value, null), "the send path stamps the owner");
        await SyncAsync(streamA, streamO, streamU, streamS);

        streamA.Received(reply.MessageId).Should().BeTrue("the owner receives its own reply");
        streamS.Received(reply.MessageId).Should().BeTrue("a monitor receives every message");
        streamO.Received(reply.MessageId).Should().BeFalse("an offer of another conversation does not open this one");
        streamU.Received(reply.MessageId).Should().BeFalse("the unrelated agent does not receive it");
    }

    [Fact]
    public async Task StreamEvents_ShouldDeliverEveryMessage_WhenTheSubscriberIsAQualityAnalystOrASupervisorWithoutAnAgentProfile()
    {
        var (_, agentA) = _factory.NewAgent("readers-owner");
        var (userU, _) = _factory.NewAgent("readers-unrelated");
        var qa = QualityAnalyst();
        var supervisorUser = _factory.NewUser(UserRole.Supervisor, "readers-supervisor");
        var owned = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active);
        var queued = _factory.SeedConversation(ConversationOwner.ForQueue(_factory.SeedQueue().QueueId), ConversationState.Queued);
        await using var streamQ = await _factory.OpenStreamAsync("quality analyst", qa);
        await using var streamS = await _factory.OpenStreamAsync("supervisor", supervisorUser);
        await using var streamU = await _factory.OpenStreamAsync("unrelated U", userU);
        using var capture = new MessageEventCapture(_factory.Bus);
        using var supervisor = _factory.ClientFor(supervisorUser);

        foreach (var conversation in new[] { owned, queued })
        {
            (await supervisor.PostAsJsonAsync(
                    $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/note", new { text = "check this" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var toOwned = await capture.WaitForAsync(e => e.ConversationId == owned.ConversationId.Value, "the owned note");
        var toQueued = await capture.WaitForAsync(e => e.ConversationId == queued.ConversationId.Value, "the queued note");
        toQueued.Audience.Should().Be(ConversationAudience.None, "a queued conversation with no offer names no agent");
        await SyncAsync(streamQ, streamS, streamU);

        foreach (var message in new[] { toOwned, toQueued })
        {
            streamQ.Received(message.MessageId).Should().BeTrue("a Quality Analyst (read) receives every message");
            streamS.Received(message.MessageId).Should().BeTrue("a Supervisor (monitor) receives every message");
            streamU.Received(message.MessageId).Should().BeFalse("an agent holding nothing of it does not");
        }
    }

    [Fact]
    public async Task AgentAssistEvents_ShouldReachTheNamedAgentAndReadersButNotTheOwner_WhenTheCallsAgentIsNotTheOwner()
    {
        var (userA, agentA) = _factory.NewAgent("assist-on-call");
        var (userO, agentO) = _factory.NewAgent("assist-owner");
        var supervisorUser = _factory.NewUser(UserRole.Supervisor, "assist-reader");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(agentO.AgentId), ConversationState.Active, channel: ChannelType.Voice);
        await using var streamA = await _factory.OpenStreamAsync("named agent A", userA);
        await using var streamO = await _factory.OpenStreamAsync("owner O", userO);
        await using var streamS = await _factory.OpenStreamAsync("supervisor", supervisorUser);
        var id = conversation.ConversationId.Value;

        foreach (var evt in AgentAssistEvents(agentA.AgentId.Value, id))
            _factory.Bus.Publish(evt);
        await SyncAsync(streamA, streamO, streamS);

        foreach (var type in AgentAssistTypes)
        {
            ReceivedFrame(streamA, type, id).Should().BeTrue($"the agent {type} names receives it");
            ReceivedFrame(streamS, type, id).Should().BeTrue($"a monitor receives {type}");
            ReceivedFrame(streamO, type, id).Should().BeFalse($"the owner is not the agent {type} names");
        }

        var unnamed = $"{id}-unnamed";
        foreach (var evt in AgentAssistEvents("", unnamed))
            _factory.Bus.Publish(evt);
        await SyncAsync(streamA, streamO, streamS);

        foreach (var type in AgentAssistTypes)
        {
            ReceivedFrame(streamS, type, unnamed).Should().BeTrue($"an empty agentId still reaches the readers ({type})");
            ReceivedFrame(streamA, type, unnamed).Should().BeFalse($"an empty agentId names no agent ({type})");
            ReceivedFrame(streamO, type, unnamed).Should().BeFalse($"an empty agentId names no agent ({type})");
        }
    }

    [Fact]
    public async Task CsatResponseRecorded_ShouldReachOnlyReaders_WhenTheConversationsLastOwnerIsConnected()
    {
        var (userA, agentA) = _factory.NewAgent("csat-last-owner");
        var supervisorUser = _factory.NewUser(UserRole.Supervisor, "csat-reader");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Closed);
        await using var streamA = await _factory.OpenStreamAsync("last owner A", userA);
        await using var streamS = await _factory.OpenStreamAsync("supervisor", supervisorUser);
        var id = conversation.ConversationId.Value;

        _factory.Bus.Publish(new CsatResponseRecordedEvent(
            Tenant, $"r-{Guid.NewGuid():N}", "s-1", id, "WebChat", "Sales", 2, "the agent was rude", DateTimeOffset.UtcNow));
        await SyncAsync(streamA, streamS);

        ReceivedFrame(streamS, "csat.response_recorded", id).Should().BeTrue("a monitor receives the survey answer");
        ReceivedFrame(streamA, "csat.response_recorded", id).Should().BeFalse("no agent is the audience of a survey answer");
    }

    private static readonly string[] AgentAssistTypes =
        ["agentassist.suggestion", "agentassist.sentiment", "agentassist.compliance_alert", "agentassist.transcript"];

    private static PlatformEvent[] AgentAssistEvents(string agentId, string conversationId) =>
    [
        new AgentAssistSuggestionEvent(Tenant, "as-1", agentId, conversationId, "sg-1", "Offer a refund", "Important", "Keyword", null),
        new AgentAssistSentimentEvent(Tenant, "as-1", agentId, conversationId, "Caller", -0.6f, "Frustrated", ["again"]),
        new AgentAssistComplianceAlertEvent(Tenant, "as-1", agentId, conversationId, "cr-1", "full card number", "Warning"),
        new AgentAssistTranscriptEvent(Tenant, "as-1", agentId, conversationId, "Caller", "third time I call", IsFinal: true),
    ];
}

/// <summary>
/// contact-history-opens-by-assignment task 2.8, spec audit round 2 F3: the post-transfer audience read of the note sits
/// after the move committed, so neither a failing read nor a request cancelled at that point may skip the note or the
/// <c>conversation.transferred</c> audit. A failed read publishes the stored note with an empty audience (readers only,
/// fail closed), as any publisher that cannot name its audience does.
/// </summary>
public sealed class TransferNoteAudienceReadTests : IClassFixture<DetachedContentDeliveryApiFactory>
{
    private const string Note = "Customer disputes the October invoice; please do not re-issue it.";

    private readonly DetachedContentDeliveryApiFactory _factory;

    public TransferNoteAudienceReadTests(DetachedContentDeliveryApiFactory factory) => _factory = factory;

    private static object ToAgent(Agent agent) =>
        new { targetQueueId = default(string), targetAgentId = agent.AgentId.Value, note = Note };

    private async Task<IReadOnlyList<Message>> MessagesOfAsync(EntityId conversationId) =>
        await _factory.Services.GetRequiredService<IMessageStore>().GetConversationMessagesAsync(
            ConversationOwnershipApiFactory.Tenant, conversationId, 100, 0, CancellationToken.None);

    [Fact]
    public async Task Transfer_ShouldStoreAuditAndPublishTheNoteToReadersOnly_WhenThePostTransferAudienceReadThrows()
    {
        var (userA, agentA) = _factory.NewAgent("failing-read-from");
        var (userB, agentB) = _factory.NewAgent("failing-read-to");
        var supervisorUser = _factory.NewUser(UserRole.Supervisor, "failing-read-reader");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active, channel: ChannelType.WhatsApp);
        var armed = 1;
        var fired = false;
        _factory.ConversationProbe.OnRead(conversation.ConversationId, (row, _) =>
        {
            if (row?.Owner == ConversationOwner.ForAgent(agentB.AgentId) && Interlocked.Exchange(ref armed, 0) == 1)
            {
                fired = true;
                throw new InvalidOperationException("the conversation store is unreachable");
            }
        });
        await using var streamA = await _factory.OpenStreamAsync("former owner A", userA);
        await using var streamB = await _factory.OpenStreamAsync("new owner B", userB);
        await using var streamS = await _factory.OpenStreamAsync("supervisor", supervisorUser);
        using var capture = new MessageEventCapture(_factory.Bus);
        using var clientA = _factory.ClientFor(userA);

        var response = await clientA.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation.ConversationId.Value}/transfer", ToAgent(agentB));

        fired.Should().BeTrue("the handler reads the conversation again after the move, to stamp the note's audience");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(agentB.AgentId));
        (await MessagesOfAsync(conversation.ConversationId)).Should().ContainSingle("the note is stored");
        _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(entry => entry.Action == "conversation.transferred")
            .Which.Metadata!["note"].Should().Be("written");
        var note = await capture.WaitForAsync(
            e => e.ConversationId == conversation.ConversationId.Value && e.Sender == "System", "the transfer note");
        note.Audience.Should().Be(ConversationAudience.None, "an audience that cannot be read is empty (fail closed)");
        await LiveStreamProbe.SyncAsync(_factory.Bus, ConversationOwnershipApiFactory.Tenant.Value, streamA, streamB, streamS);

        streamS.Received(note.MessageId).Should().BeTrue("the readers still receive the note");
        streamB.Received(note.MessageId).Should().BeFalse("with no audience read, no agent receives it live");
        streamA.Received(note.MessageId).Should().BeFalse("the former owner never does");
    }

    [Fact]
    public async Task Transfer_ShouldStillStoreAndAuditTheNote_WhenTheRequestIsCancelledAfterTheMoveCommitted()
    {
        var (userA, agentA) = _factory.NewAgent("cancelled-from");
        var (_, agentB) = _factory.NewAgent("cancelled-to");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active, channel: ChannelType.WhatsApp);
        using var hangUp = new CancellationTokenSource();
        var armed = 1;
        var fired = false;
        _factory.ConversationProbe.OnRead(conversation.ConversationId, (row, ct) =>
        {
            if (row?.Owner == ConversationOwner.ForAgent(agentB.AgentId) && Interlocked.Exchange(ref armed, 0) == 1)
            {
                fired = true;
                hangUp.Cancel(); // the client hangs up once the move has committed
                ct.ThrowIfCancellationRequested(); // a store honours the token it is given, as Npgsql does
            }
        });
        using var clientA = _factory.ClientFor(userA);

        try
        {
            using var response = await clientA.PostAsJsonAsync(
                $"/api/v1/conversations/{conversation.ConversationId.Value}/transfer", ToAgent(agentB), hangUp.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the client hung up. The server carries on with what the move already committed.
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline && _factory.AuditOf(conversation.ConversationId).Count == 0)
            await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until the server-side handler has finished

        fired.Should().BeTrue("the handler reads the conversation again after the move, to stamp the note's audience");
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(agentB.AgentId));
        (await MessagesOfAsync(conversation.ConversationId)).Should().ContainSingle("a client that hangs up does not lose the note");
        _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(entry => entry.Action == "conversation.transferred")
            .Which.Metadata!["note"].Should().Be("written");
    }
}
