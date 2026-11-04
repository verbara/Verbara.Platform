using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// <see cref="WebhookInboundProcessor"/> in isolation: what a bot that owns the conversation decides after a
/// customer's message (reply, hand-off to a queue, end), and that a failure after a message is stored is
/// contained to that message while a storage failure is not.
/// </summary>
public sealed class WebhookInboundProcessorTests : IDisposable
{
    private static readonly TenantId Tenant = new("t-processor");
    private static readonly EntityId ContactId = EntityId.From("contact-processor");
    private static readonly EntityId BotId = EntityId.From("bot-processor");
    private static readonly EntityId QueueId = EntityId.From("queue-processor");

    private readonly IInboundMessagePipeline _pipeline = Substitute.For<IInboundMessagePipeline>();
    private readonly IInboundRouter _router = Substitute.For<IInboundRouter>();
    private readonly IConversationSwitchboard _switchboard = Substitute.For<IConversationSwitchboard>();
    private readonly IConversationService _conversationService = Substitute.For<IConversationService>();
    private readonly InMemoryConversationStore _conversations = new();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly IVirtualAgent _bot = Substitute.For<IVirtualAgent>();
    private readonly IConversationLifecycleService _lifecycle = Substitute.For<IConversationLifecycleService>();
    private readonly IMessageStore _messages = Substitute.For<IMessageStore>();
    private readonly DeliveryStatusHandler _statuses;
    private readonly PlatformEventBus _eventBus = new();

    public WebhookInboundProcessorTests()
    {
        _statuses = new DeliveryStatusHandler(_messages, NullLogger<DeliveryStatusHandler>.Instance);
        _contacts.GetByIdAsync(Tenant, ContactId, Arg.Any<CancellationToken>())
            .Returns(new Contact { ContactId = ContactId, TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow });
    }

    public void Dispose()
    {
        _statuses.Dispose();
        _eventBus.Dispose();
    }

    private WebhookInboundProcessor CreateSut() =>
        new(_pipeline, _router, _switchboard, _conversationService, _conversations, _contacts, _bot, _lifecycle,
            _statuses, _eventBus, Announcer(), NullLogger<WebhookInboundProcessor>.Instance);

    private CustomerReturnAnnouncer Announcer() =>
        new(_eventBus, Substitute.For<Verbara.Platform.Audit.IAuditService>(), new ConversationCloseMetrics(),
            NullLogger<CustomerReturnAnnouncer>.Instance);

    private async Task<Conversation> SeedBotOwnedAsync()
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = ContactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued,
            Owner = ConversationOwner.ForBot(BotId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _conversations.SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    private InboundMessage Inbound(string externalId, Conversation conversation)
    {
        var message = new InboundMessage(
            new ChannelAddress(ChannelType.WhatsApp, "15550000002"),
            new MessageEnvelope([new TextBlock("hola")]),
            externalId,
            DateTimeOffset.UtcNow);
        _pipeline.ProcessAsync(message, Tenant, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(new PipelineResult(conversation.ConversationId, ContactId, EntityId.New(), IsNewConversation: false));
        return message;
    }

    private static WebhookResult Delivery(params InboundMessage[] messages) => WebhookResult.From(messages, []);

    [Fact]
    public async Task ProcessAsync_ShouldSendEachReply_WhenBotReplies()
    {
        var conversation = await SeedBotOwnedAsync();
        MessageEnvelope[] replies = [new([new TextBlock("uno")]), new([new TextBlock("dos")])];
        _bot.ProcessMessageAsync(conversation.ConversationId, Tenant, Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(new BotResponse(BotResponseAction.Reply, replies, null, null, null));

        await CreateSut().ProcessAsync(Tenant, ChannelType.WhatsApp, Delivery(Inbound("wamid.r1", conversation)), CancellationToken.None);

        await _conversationService.Received(2).SendMessageAsync(
            conversation.ConversationId, Tenant, Arg.Any<MessageEnvelope>(), BotId, ConversationOwnerKind.Bot, Arg.Any<CancellationToken>());
        await _router.DidNotReceiveWithAnyArgs().RouteAsync(default!, default);
    }

    [Fact]
    public async Task ProcessAsync_ShouldTransferWithFlowMetadata_WhenBotHandsOffToQueue()
    {
        var conversation = await SeedBotOwnedAsync();
        _bot.ProcessMessageAsync(conversation.ConversationId, Tenant, Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(new BotResponse(BotResponseAction.TransferToQueue, null, QueueId, null, "handoff",
                new Dictionary<string, string> { ["reasonPath"] = "billing/refund" }));

        await CreateSut().ProcessAsync(Tenant, ChannelType.WhatsApp, Delivery(Inbound("wamid.t1", conversation)), CancellationToken.None);

        await _switchboard.Received(1).TransferToQueueAsync(conversation.ConversationId, Tenant, QueueId, Arg.Any<CancellationToken>());
        (await _conversations.GetByIdAsync(Tenant, conversation.ConversationId, CancellationToken.None))!
            .Metadata.Should().Contain("reasonPath", "billing/refund");
    }

    [Fact]
    public async Task ProcessAsync_ShouldNotTransfer_WhenBotHandsOffWithoutQueue()
    {
        var conversation = await SeedBotOwnedAsync();
        _bot.ProcessMessageAsync(conversation.ConversationId, Tenant, Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(new BotResponse(BotResponseAction.TransferToQueue, null, null, null, null));

        await CreateSut().ProcessAsync(Tenant, ChannelType.WhatsApp, Delivery(Inbound("wamid.t2", conversation)), CancellationToken.None);

        await _switchboard.DidNotReceiveWithAnyArgs().TransferToQueueAsync(default, default, default, default);
    }

    [Fact]
    public async Task ProcessAsync_ShouldCloseConversation_WhenBotEndsIt()
    {
        var conversation = await SeedBotOwnedAsync();
        _bot.ProcessMessageAsync(conversation.ConversationId, Tenant, Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(new BotResponse(BotResponseAction.EndConversation, null, null, null, null));

        await CreateSut().ProcessAsync(Tenant, ChannelType.WhatsApp, Delivery(Inbound("wamid.e1", conversation)), CancellationToken.None);

        await _lifecycle.Received(1).CloseAsync(Tenant, conversation.ConversationId, Arg.Any<CancellationToken>());
    }

    // conversations-close-after-inactivity task 1.3 (design D3): the bot's EndConversation over the REAL
    // DefaultConversationLifecycleService on a Bot-owned Active row gets the outcome main gives — the lifecycle's
    // EnsureTransition throws (Active -> Closed is not an edge), the per-message catch contains it, ProcessAsync
    // returns — and the row stays Active with no -> Closed event. An added edge would close it unannounced.
    [Fact]
    public async Task ProcessAsync_ShouldLeaveTheRowActiveWithNoClosedEvent_WhenBotEndsAnActiveConversationWithTheRealLifecycle()
    {
        var conversation = await SeedBotOwnedAsync();
        conversation.State = ConversationState.Active;
        await _conversations.SaveAsync(conversation, CancellationToken.None);
        _bot.ProcessMessageAsync(conversation.ConversationId, Tenant, Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(new BotResponse(BotResponseAction.EndConversation, null, null, null, null));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var closed = new List<ConversationStateChangedEvent>();
        using var subscription = _eventBus.Events.Subscribe(e =>
        {
            if (e is ConversationStateChangedEvent { NewState: "Closed" } changed)
                closed.Add(changed);
        });
        var sut = new WebhookInboundProcessor(_pipeline, _router, _switchboard, _conversationService, _conversations,
            _contacts, _bot, new DefaultConversationLifecycleService(_conversations, clock), _statuses, _eventBus,
            Announcer(), NullLogger<WebhookInboundProcessor>.Instance);

        var act = () => sut.ProcessAsync(
            Tenant, ChannelType.WhatsApp, Delivery(Inbound("wamid.end-active", conversation)), CancellationToken.None);

        await act.Should().NotThrowAsync("the dispatch failure is contained to the message, as on main");
        var after = await _conversations.GetByIdAsync(Tenant, conversation.ConversationId, CancellationToken.None);
        after!.State.Should().Be(ConversationState.Active);
        after.ClosedAt.Should().BeNull();
        closed.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsync_ShouldContinueWithNextMessage_WhenBotThrowsForOne()
    {
        var first = await SeedBotOwnedAsync();
        var second = await SeedBotOwnedAsync();
        _bot.ProcessMessageAsync(first.ConversationId, Tenant, Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("bot backend down"));
        _bot.ProcessMessageAsync(second.ConversationId, Tenant, Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(new BotResponse(BotResponseAction.EndConversation, null, null, null, null));

        var act = () => CreateSut().ProcessAsync(
            Tenant, ChannelType.WhatsApp, Delivery(Inbound("wamid.x1", first), Inbound("wamid.x2", second)), CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _lifecycle.Received(1).CloseAsync(Tenant, second.ConversationId, Arg.Any<CancellationToken>());
    }

    // ─── A conversation read that fails still publishes the stored message, to the readers only (design D17, spec audit F3) ───

    private WebhookInboundProcessor CreateSutReadingFrom(IConversationStore conversations) =>
        new(_pipeline, _router, _switchboard, _conversationService, conversations, _contacts, _bot, _lifecycle,
            _statuses, _eventBus, Announcer(), NullLogger<WebhookInboundProcessor>.Instance);

    public static TheoryData<Exception> FailedReads() => new()
    {
        new InvalidOperationException("database unavailable"),
        new TaskCanceledException("a command timeout, not this delivery's token"),
    };

    [Theory]
    [MemberData(nameof(FailedReads))]
    public async Task ProcessAsync_ShouldPublishTheStoredMessageWithAnEmptyAudience_WhenReadingTheConversationThrows(Exception failure)
    {
        var conversation = await SeedBotOwnedAsync();
        var inbound = Inbound("wamid.r1", conversation);
        var conversations = Substitute.For<IConversationStore>();
        conversations.GetByIdAsync(Tenant, conversation.ConversationId, Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var published = new List<PlatformEvent>();
        using var subscription = _eventBus.Events.Subscribe(published.Add);

        var act = () => CreateSutReadingFrom(conversations).ProcessAsync(Tenant, ChannelType.WhatsApp, Delivery(inbound), CancellationToken.None);

        await act.Should().NotThrowAsync("the message is stored: a provider retry would only find a duplicate");
        var message = published.OfType<ConversationMessageEvent>().Should().ContainSingle().Subject;
        message.ConversationId.Should().Be(conversation.ConversationId.Value);
        message.Audience.Should().Be(ConversationAudience.None, "readers only: an unread conversation names no agent (fail closed)");
        await _router.DidNotReceiveWithAnyArgs().RouteAsync(default!, default);
        await _bot.DidNotReceiveWithAnyArgs().ProcessMessageAsync(default, default, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_ShouldPropagateAndPublishNothing_WhenTheDeliveryIsCancelledDuringTheRead()
    {
        var conversation = await SeedBotOwnedAsync();
        var inbound = Inbound("wamid.r2", conversation);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var conversations = Substitute.For<IConversationStore>();
        conversations.GetByIdAsync(Tenant, conversation.ConversationId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));
        var published = new List<PlatformEvent>();
        using var subscription = _eventBus.Events.Subscribe(published.Add);

        var act = () => CreateSutReadingFrom(conversations).ProcessAsync(Tenant, ChannelType.WhatsApp, Delivery(inbound), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("the delivery's own cancellation ends it, as on every other step");
        published.OfType<ConversationMessageEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsync_ShouldPropagate_WhenStoringAMessageFails()
    {
        var conversation = await SeedBotOwnedAsync();
        var inbound = Inbound("wamid.s1", conversation);
        _pipeline.ProcessAsync(inbound, Tenant, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var act = () => CreateSut().ProcessAsync(Tenant, ChannelType.WhatsApp, Delivery(inbound), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("the provider must retry a delivery that was not stored");
        await _bot.DidNotReceiveWithAnyArgs().ProcessMessageAsync(default, default, default!, default);
    }
}
