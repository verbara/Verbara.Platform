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
            _statuses, _eventBus, NullLogger<WebhookInboundProcessor>.Instance);

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
