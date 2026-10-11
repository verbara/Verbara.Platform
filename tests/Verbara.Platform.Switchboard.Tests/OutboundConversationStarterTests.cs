using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// agent-started-conversations (design D14): the agent starts a conversation only in its own name; the
/// connector sends first and nothing is persisted on failure; a success is saved through the conversation
/// store (no licensed-agent ledger row), born <see cref="ConversationState.Active"/> and owned by the agent,
/// with its Sent message stored right after the send; an open conversation owned elsewhere is refused, one the
/// caller owns is reused when it can take a message and refused with a code when it cannot.
/// </summary>
public sealed class OutboundConversationStarterTests : IDisposable
{
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly IMessageStore _messages = Substitute.For<IMessageStore>();
    private readonly IChannelRegistry _channels = Substitute.For<IChannelRegistry>();
    private readonly IChannelConnector _connector = Substitute.For<IChannelConnector>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly PlatformEventBus _eventBus = new();

    private readonly TenantId _tenantId = new("tenant-1");
    private readonly EntityId _contactId = EntityId.From("contact-001");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private const string ActorUserId = "user-of-agent-001";
    private const string Address = "+15550001111";
    private readonly DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public OutboundConversationStarterTests()
    {
        _clock.UtcNow.Returns(_now);
        _channels.AvailableChannels.Returns([ChannelType.WhatsApp, ChannelType.WebChat]);
        _channels.GetConnector(Arg.Any<ChannelType>()).Returns(_connector);
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(true, "wamid.out-1", null, null));
        _capacity.TryReserveAsync(_tenantId, _agentId, Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _conversations.FindLastInboundAtAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(_now.AddHours(-1));

        var contact = new Contact { ContactId = _contactId, TenantId = _tenantId, CreatedAt = _now };
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, Address));
        _contacts.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>()).Returns(contact);
    }

    public void Dispose() => _eventBus.Dispose();

    private OutboundConversationStarter CreateSut() =>
        new(_conversations, _contacts, _messages, _channels, _capacity, _clock, _eventBus,
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

    // Nothing is stored, and no capacity stays taken: a unit taken before the send was given back.
    private async Task NothingPersistedAsync(bool unitTakenBeforeSend = false)
    {
        await _conversations.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _messages.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _capacity.DidNotReceiveWithAnyArgs().ReserveAsync(default, default, default, default);
        await _capacity.Received(unitTakenBeforeSend ? 1 : 0).ReleaseAsync(
            _tenantId, _agentId, ChannelType.WhatsApp, CancellationToken.None);
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
        await NothingPersistedAsync(unitTakenBeforeSend: true);
    }

    [Fact]
    public async Task StartAsync_ShouldSaveBornActiveOwnedConversationThroughStore_WhenSendSucceeds()
    {
        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.IsRefused.Should().BeFalse();
        await _conversations.Received(1).SaveAsync(
            Arg.Is<Conversation>(c => c.State == ConversationState.Active
                && c.Owner == ConversationOwner.ForAgent(_agentId)
                && c.CreatedBy == ActorUserId),
            CancellationToken.None);
        await _capacity.Received(1).TryReserveAsync(_tenantId, _agentId, ChannelType.WhatsApp, Arg.Any<CancellationToken>());
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default, default, default, default);
        await _messages.Received(1).SaveAsync(
            Arg.Is<Message>(m => m.DeliveryStatus == MessageDeliveryStatus.Sent && m.ExternalMessageId == "wamid.out-1"),
            CancellationToken.None);
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
        await _conversations.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _capacity.DidNotReceiveWithAnyArgs().TryReserveAsync(default, default, default, default);
    }

    [Fact]
    public async Task StartAsync_ShouldRequireTemplate_WhenWhatsAppWindowClosed()
    {
        _conversations.FindLastInboundAtAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(_now.AddHours(-30));

        var outcome = await CreateSut().StartAsync(Request(templateName: null), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.WhatsAppTemplateRequired);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await NothingPersistedAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldRefuse_WhenAgentHasNoCapacity()
    {
        _capacity.TryReserveAsync(_tenantId, _agentId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
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
        await NothingPersistedAsync(unitTakenBeforeSend: true);
    }

    [Fact]
    public async Task StartAsync_ShouldReturnPersistFailedAndReleaseCapacity_WhenConversationSaveFailsAfterSend()
    {
        _conversations.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("conversations table unavailable"));

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.StartPersistFailed);
        await _capacity.Received(1).ReleaseAsync(_tenantId, _agentId, ChannelType.WhatsApp, CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldReleaseCapacity_WhenCancelledDuringSend()
    {
        using var cts = new CancellationTokenSource();
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns<SendResult>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var act = () => CreateSut().StartAsync(Request(templateName: "hello_world"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await NothingPersistedAsync(unitTakenBeforeSend: true);
    }

    [Fact]
    public async Task StartAsync_ShouldReadWindowPerContact_WhenStartingOnWhatsApp()
    {
        _conversations.FindLastInboundAtAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(_now.AddHours(-23));

        var outcome = await CreateSut().StartAsync(Request(templateName: null), CancellationToken.None);

        outcome.IsRefused.Should().BeFalse("the contact wrote within 24 hours, so free-form text may go out");
        await _connector.Received(1).SendAsync(Arg.Is<OutboundMessage>(m => m.TemplateId == null), Arg.Any<CancellationToken>());
        outcome.Message!.Content.Blocks.OfType<TextBlock>().Single().Text.Should().Be("Hello from your agent");
    }

    [Fact]
    public async Task StartAsync_ShouldStoreTemplateTranscript_WhenTemplateNamed()
    {
        var outcome = await CreateSut().StartAsync(
            Request(templateName: "hello_world") with { TemplateLanguage = "es_MX" }, CancellationToken.None);

        outcome.Message!.Content.Blocks.OfType<TextBlock>().Single().Text.Should().Be("[template hello_world es_MX]");
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

    // ─── fix round: persistence after a successful send ──────────────────────

    [Fact]
    public async Task StartAsync_ShouldReturnPersistFailedAndReleaseCapacity_WhenMessageSaveFailsAfterSend()
    {
        _messages.SaveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("messages table unavailable"));

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.StartPersistFailed);
        await _conversations.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _capacity.Received(1).ReleaseAsync(_tenantId, _agentId, ChannelType.WhatsApp, CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldPersistWithoutCancellation_WhenClientAbortsAfterProviderAccepted()
    {
        using var cts = new CancellationTokenSource();
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel(); // the client goes away while the provider is accepting the message
                return new SendResult(true, "wamid.out-1", null, null);
            });
        _messages.SaveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<CancellationToken>(1).IsCancellationRequested
                ? Task.FromCanceled(ci.ArgAt<CancellationToken>(1))
                : Task.CompletedTask);

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), cts.Token);

        outcome.IsRefused.Should().BeFalse("the provider side effect cannot be undone, so persistence completes");
        outcome.Message!.ExternalMessageId.Should().Be("wamid.out-1");
        await _conversations.Received(1).SaveAsync(Arg.Any<Conversation>(), CancellationToken.None);
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default, default, default, default);
    }

    [Fact]
    public async Task StartAsync_ShouldSaveSentMessageRightAfterSend_WhenSendSucceeds()
    {
        var calls = new List<string>();
        _connector.When(c => c.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())).Do(_ => calls.Add("send"));
        _messages.When(m => m.SaveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())).Do(_ => calls.Add("message"));
        _conversations.When(c => c.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())).Do(_ => calls.Add("conversation"));
        _capacity.WhenForAnyArgs(c => c.TryReserveAsync(default, default, default, default)).Do(_ => calls.Add("reserve"));

        await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        // Meta's sent/delivered callback can arrive as soon as the send returns (design D8): the row it looks for
        // by provider id is the first write after the send.
        calls.Should().ContainInOrder("send", "message", "conversation");
        calls.IndexOf("message").Should().Be(calls.IndexOf("send") + 1);
    }

    [Theory]
    [InlineData(ConversationState.WrapUp)]
    [InlineData(ConversationState.Snoozed)]
    [InlineData(ConversationState.WaitingForCustomer)]
    public async Task StartAsync_ShouldRefuseNotSendable_WhenCallerOwnsConversationInState(ConversationState state)
    {
        var open = OpenConversation(ConversationOwner.ForAgent(_agentId), state);

        var outcome = await CreateSut().StartAsync(Request(templateName: "hello_world"), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.ConversationNotSendable);
        outcome.OpenConversationId.Should().Be(open.ConversationId);
        outcome.OpenOwnerKind.Should().Be(ConversationOwnerKind.Agent);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await NothingPersistedAsync();
    }

    [Fact]
    public async Task StartAsync_ShouldCreateOneConversation_WhenSameAgentStartsTwiceConcurrently()
    {
        var stored = new System.Collections.Concurrent.ConcurrentDictionary<EntityId, Conversation>();
        void Store(Conversation c) => stored[c.ConversationId] = c;
        _conversations.When(c => c.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())).Do(ci => Store(ci.Arg<Conversation>()));
        _conversations.FindActiveByContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(_ => stored.Values.FirstOrDefault());

        var firstSendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref sends) == 1)
                {
                    firstSendEntered.SetResult();
                    await releaseFirstSend.Task;
                }
                return new SendResult(true, $"wamid.out-{sends}", null, null);
            });

        var sut = CreateSut();
        var first = sut.StartAsync(Request(templateName: "hello_world"), CancellationToken.None);
        await firstSendEntered.Task;
        var second = sut.StartAsync(Request(templateName: "hello_world"), CancellationToken.None);
        await Task.WhenAny(second, Task.Delay(300)); // fence-allow: SETTLE — lets an unserialised second start run ahead of the first; the assertions hold whatever the timing
        releaseFirstSend.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        stored.Should().ContainSingle("a double submit must not open two conversations with the same contact");
        outcomes.Select(o => o.Conversation!.ConversationId).Distinct().Should().ContainSingle();
        outcomes.Count(o => o.Reused).Should().Be(1, "the second start finds the first one's conversation");
    }
}
