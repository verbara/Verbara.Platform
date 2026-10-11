using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// agent-started-conversations (design D14): the agent starts a conversation only in its own name; the
/// connector sends first and nothing is persisted on failure; a success is committed through metering's
/// ownership writer, born <see cref="ConversationState.Active"/> and owned by the agent; an open
/// conversation owned elsewhere is refused, one the caller owns is reused.
/// </summary>
public sealed class OutboundConversationStarterTests : IDisposable
{
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly IMessageStore _messages = Substitute.For<IMessageStore>();
    private readonly IChannelRegistry _channels = Substitute.For<IChannelRegistry>();
    private readonly IChannelConnector _connector = Substitute.For<IChannelConnector>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly IWhatsAppSessionWindow _window = Substitute.For<IWhatsAppSessionWindow>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly RecordingOwnershipWriter _ownership;

    private readonly TenantId _tenantId = new("tenant-1");
    private readonly EntityId _contactId = EntityId.From("contact-001");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private const string ActorUserId = "user-of-agent-001";
    private const string Address = "+15550001111";
    private readonly DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public OutboundConversationStarterTests()
    {
        _ownership = new RecordingOwnershipWriter(_connector);
        _clock.UtcNow.Returns(_now);
        _channels.AvailableChannels.Returns([ChannelType.WhatsApp, ChannelType.WebChat]);
        _channels.GetConnector(Arg.Any<ChannelType>()).Returns(_connector);
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(true, "wamid.out-1", null, null));
        _capacity.HasCapacityAsync(_tenantId, _agentId, Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _window.DecideAsync(default, default, default, default)
            .ReturnsForAnyArgs(new WhatsAppWindowDecision(WhatsAppSendMode.FreeForm, _now.AddHours(-1)));

        var contact = new Contact { ContactId = _contactId, TenantId = _tenantId, CreatedAt = _now };
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, Address));
        _contacts.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>()).Returns(contact);
    }

    public void Dispose() => _eventBus.Dispose();

    private OutboundConversationStarter CreateSut() =>
        new(_conversations, _contacts, _messages, _channels, _capacity, _window, _ownership, _clock, _eventBus,
            NullLogger<OutboundConversationStarter>.Instance);

    private OutboundStartRequest Request(string? templateName = null) =>
        new(_tenantId, _contactId, ChannelType.WhatsApp, _agentId, ActorUserId,
            new MessageEnvelope([new TextBlock("Hello from your agent")]), templateName);

    private Conversation OpenConversation(ConversationOwner? owner, ConversationState state = ConversationState.Active)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From("conv-open"),
            TenantId = _tenantId,
            ContactId = _contactId,
            Channel = ChannelType.WhatsApp,
            State = state,
            Owner = owner,
            CreatedAt = _now.AddHours(-2),
        };
        _conversations.FindActiveByContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(conversation);
        return conversation;
    }

    private async Task NothingPersistedAsync()
    {
        await _conversations.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _messages.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _capacity.DidNotReceiveWithAnyArgs().ReserveAsync(default, default, default, default);
        _ownership.Commits.Should().BeEmpty("nothing is committed through the ownership writer");
    }

    // ─── all-or-nothing ───────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ShouldPersistNothing_WhenConnectorFails()
    {
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(false, null, "131026", "Message undeliverable"));

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.ChannelSendFailed);
        await _connector.Received(1).SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
        await NothingPersistedAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldCommitThroughOwnershipWriter_WhenSendSucceeds()
    {
        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.IsRefused.Should().BeFalse();
        var commit = _ownership.Commits.Should().ContainSingle("the conversation is committed by metering's writer").Subject;
        commit.State.Should().Be(ConversationState.Active, "the conversation is born Active");
        commit.Owner.Should().Be(ConversationOwner.ForAgent(_agentId), "it is born owned by the starting agent");
        commit.Change.ActorUserId.Should().Be(ActorUserId, "the agent starts it in its own name");
        // OwnershipChangeKind is deliberately not asserted here: D14 adds no new kind, and which existing
        // kind (if any) an own-name start is recorded under is decided in task 7.3.
        commit.ConnectorCalledBefore.Should().BeTrue("the connector sends first, the commit follows");
        await _conversations.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _capacity.Received(1).ReserveAsync(_tenantId, _agentId, ChannelType.WhatsApp, Arg.Any<CancellationToken>());
        await _messages.Received(1).SaveAsync(
            Arg.Is<Message>(m => m.DeliveryStatus == MessageDeliveryStatus.Sent && m.ExternalMessageId == "wamid.out-1"),
            Arg.Any<CancellationToken>());
    }

    // ─── refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ShouldRefuseOpenElsewhere_WhenAnotherAgentOwns()
    {
        var other = EntityId.From("agent-other");
        var open = OpenConversation(ConversationOwner.ForAgent(other));

        var outcome = await CreateSut().StartAsync(Request(), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.ConversationOpenElsewhere);
        outcome.OpenConversationId.Should().Be(open.ConversationId);
        outcome.OpenOwnerKind.Should().Be(ConversationOwnerKind.Agent);
        open.Owner.Should().Be(ConversationOwner.ForAgent(other), "there is no takeover");
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await NothingPersistedAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldReuse_WhenCallerOwnsOpenConversation()
    {
        var open = OpenConversation(ConversationOwner.ForAgent(_agentId));

        var outcome = await CreateSut().StartAsync(Request(), CancellationToken.None);

        outcome.IsRefused.Should().BeFalse();
        outcome.Reused.Should().BeTrue();
        outcome.Conversation!.ConversationId.Should().Be(open.ConversationId);
        outcome.Message!.DeliveryStatus.Should().Be(MessageDeliveryStatus.Sent);
        await _connector.Received(1).SendAsync(
            Arg.Is<OutboundMessage>(m => m.ConversationId == open.ConversationId), Arg.Any<CancellationToken>());
        await _conversations.DidNotReceive().SaveAsync(
            Arg.Is<Conversation>(c => c.ConversationId != open.ConversationId), Arg.Any<CancellationToken>());
        _ownership.Commits.Should().BeEmpty("the caller already owns it: ownership does not change");
    }

    [Fact]
    public async Task StartAsync_ShouldRequireTemplate_WhenWhatsAppWindowClosed()
    {
        _window.DecideAsync(default, default, default, default)
            .ReturnsForAnyArgs(new WhatsAppWindowDecision(WhatsAppSendMode.TemplateRequired, _now.AddHours(-30)));

        var outcome = await CreateSut().StartAsync(Request(templateName: null), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.WhatsAppTemplateRequired);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await NothingPersistedAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldRefuse_WhenAgentHasNoCapacity()
    {
        _capacity.HasCapacityAsync(_tenantId, _agentId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(false);

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.AgentAtCapacity);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await NothingPersistedAsync();
    }

    // ─── further refusals and faults (task 7.3) ───────────────────────────────

    [Fact]
    public async Task StartAsync_ShouldRefuseNoAddress_WhenContactHasNoAddressOnChannel()
    {
        var bare = new Contact { ContactId = _contactId, TenantId = _tenantId, CreatedAt = _now };
        _contacts.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>()).Returns(bare);

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.ContactHasNoAddress);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await NothingPersistedAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldRefuseChannelNotAvailable_WhenNoConnectorRegistered()
    {
        _channels.AvailableChannels.Returns([ChannelType.WebChat]);

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.ChannelNotAvailable);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await NothingPersistedAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldRefuseOpenElsewhere_WhenQueuedUnowned()
    {
        var open = OpenConversation(owner: null, ConversationState.Queued);

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.ConversationOpenElsewhere);
        outcome.OpenConversationId.Should().Be(open.ConversationId);
        outcome.OpenOwnerKind.Should().BeNull("a queued conversation with no owner has no owner kind");
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await NothingPersistedAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldPersistNothing_WhenConnectorThrows()
    {
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns<SendResult>(_ => throw new HttpRequestException("provider unreachable"));

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.ChannelSendFailed);
        await NothingPersistedAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldReleaseCapacity_WhenOwnershipCommitFails()
    {
        _ownership.FailWith = new InvalidOperationException("ledger unavailable");

        var act = () => CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("ledger unavailable");
        await _capacity.Received(1).ReserveAsync(_tenantId, _agentId, ChannelType.WhatsApp, Arg.Any<CancellationToken>());
        await _capacity.Received(1).ReleaseAsync(_tenantId, _agentId, ChannelType.WhatsApp, Arg.Any<CancellationToken>());
        await _messages.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task StartAsync_ShouldPassTemplateNameAndLanguage_WhenTemplateNamed()
    {
        var request = Request(templateName: "hello_world") with { TemplateLanguage = "es_MX" };

        await CreateSut().StartAsync(request, CancellationToken.None);

        await _connector.Received(1).SendAsync(
            Arg.Is<OutboundMessage>(m => m.TemplateId == "hello_world" && m.TemplateLanguage == "es_MX" && m.To.Address == Address),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ShouldCommitUnderExistingKind_WhenSendSucceeds()
    {
        await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        var commit = _ownership.Commits.Should().ContainSingle().Subject;
        commit.Change.Kind.Should().Be(OutboundConversationStarter.OwnNameStartKind);
        // D14: no new ownership-change kind, so no new ledger kind either.
        Enum.GetValues<OwnershipChangeKind>().Should().BeEquivalentTo(
            [OwnershipChangeKind.TakenOver, OwnershipChangeKind.Transferred, OwnershipChangeKind.Reassigned]);
    }

    /// <summary>Records each commit with the conversation's state and owner at commit time, and whether the connector had already been called.</summary>
    private sealed class RecordingOwnershipWriter(IChannelConnector connector) : ILicensedAgentOwnershipWriter
    {
        public List<(EntityId Conversation, ConversationState State, ConversationOwner? Owner, OwnershipChange Change, bool ConnectorCalledBefore)> Commits { get; } = [];

        public Exception? FailWith { get; set; }

        public Task CommitOwnershipAsync(Conversation conversation, OwnershipChange change, CancellationToken ct)
        {
            if (FailWith is not null)
                return Task.FromException(FailWith);

            var sent = connector.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IChannelConnector.SendAsync));
            Commits.Add((conversation.ConversationId, conversation.State, conversation.Owner, change, sent));
            return Task.CompletedTask;
        }
    }
}
