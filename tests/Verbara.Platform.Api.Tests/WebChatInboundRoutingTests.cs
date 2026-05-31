using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Unit tests for <see cref="WebChatInboundRouter"/> — the bridge that assigns a WebChat
/// conversation to a queue on the FIRST inbound message of a session. WebChat conversations
/// are pre-created (owner-less, Queued) at session open and the inbound pipeline never routes
/// them, so the auto-distribution worker skips them. This component closes that gap, mirroring
/// the webhook inbound path (<c>WebhookEndpoints.cs:104-108</c>).
/// </summary>
public sealed class WebChatInboundRoutingTests
{
    private static readonly TenantId Tenant = new("t-webchat-001");
    private static readonly EntityId ConvId = EntityId.From("conv-wc-1");
    private static readonly EntityId ContactId = EntityId.From("contact-wc-1");
    private static readonly EntityId QueueId = EntityId.From("q-wc-1");

    private static (WebChatInboundRouter sut, InMemoryConversationStore store, string sessionId, IConversationSwitchboard switchboardMock, PlatformEventBus eventBus)
        BuildWithRealSwitchboard()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var store = new InMemoryConversationStore();
        var conversation = new Conversation
        {
            ConversationId = ConvId,
            TenantId = Tenant,
            ContactId = ContactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        // Mirrors WebChatEndpoints.CreateSession: conversation persisted owner-less in Queued.
        store.SaveAsync(conversation, CancellationToken.None).GetAwaiter().GetResult();

        var contactStore = Substitute.For<IContactStore>();
        contactStore.GetByIdAsync(Tenant, ContactId, Arg.Any<CancellationToken>())
            .Returns(new Contact { ContactId = ContactId, TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow });

        var capacity = Substitute.For<IAgentCapacityService>();
        var eventBus = new PlatformEventBus();
        var switchboard = new ConversationSwitchboard(store, capacity, clock, eventBus);

        var router = Substitute.For<IInboundRouter>();
        router.RouteAsync(default!, default).ReturnsForAnyArgs(
            new RouteResult(QueueId, default, null, null));

        var sessionManager = new WebChatSessionManager(Options.Create(new WebChatOptions()));
        var sessionId = sessionManager.ConnectAsync(
            Tenant, ConvId, new ChannelAddress(ChannelType.WebChat, "webchat-x"), clock).GetAwaiter().GetResult();

        var sut = new WebChatInboundRouter(router, switchboard, store, contactStore, eventBus, sessionManager);
        return (sut, store, sessionId, switchboard, eventBus);
    }

    private static PipelineResult FirstMessageResult() =>
        new(ConvId, ContactId, EntityId.From("msg-1"), IsNewConversation: false);

    private static MessageEnvelope Content() => new([new TextBlock("hola")]);

    [Fact]
    public async Task RouteFirstInboundAsync_ShouldAssignConversationToQueue_WhenFirstMessage()
    {
        var (sut, store, sessionId, _, _) = BuildWithRealSwitchboard();

        await sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);

        var reloaded = await store.GetByIdAsync(Tenant, ConvId, CancellationToken.None);
        reloaded.Should().NotBeNull();
        reloaded!.Owner.Should().NotBeNull();
        reloaded.Owner!.Kind.Should().Be(ConversationOwnerKind.Queue);
        reloaded.Owner.OwnerId.Should().Be(QueueId);
    }

    [Fact]
    public async Task RouteFirstInboundAsync_ShouldPublishStateChangedAndMessageEvents_WhenFirstMessage()
    {
        var (sut, _, sessionId, _, eventBus) = BuildWithRealSwitchboard();
        var captured = new List<PlatformEvent>();
        using var _sub = eventBus.Events.Subscribe(captured.Add);

        await sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);

        captured.Should().Contain(e => e is ConversationStateChangedEvent && ((ConversationStateChangedEvent)e).NewState == "Queued");
        captured.Should().Contain(e => e is ConversationMessageEvent && ((ConversationMessageEvent)e).ConversationId == ConvId.Value);
    }

    [Fact]
    public async Task RouteFirstInboundAsync_ShouldRouteOnlyOnce_WhenCalledTwiceForSameSession()
    {
        var router = Substitute.For<IInboundRouter>();
        router.RouteAsync(default!, default).ReturnsForAnyArgs(new RouteResult(QueueId, default, null, null));
        var switchboard = Substitute.For<IConversationSwitchboard>();
        var convStore = Substitute.For<IConversationStore>();
        convStore.GetByIdAsync(Tenant, ConvId, Arg.Any<CancellationToken>())
            .Returns(new Conversation { ConversationId = ConvId, TenantId = Tenant, ContactId = ContactId, Channel = ChannelType.WebChat, State = ConversationState.Queued, CreatedAt = DateTimeOffset.UtcNow });
        var contactStore = Substitute.For<IContactStore>();
        contactStore.GetByIdAsync(Tenant, ContactId, Arg.Any<CancellationToken>())
            .Returns(new Contact { ContactId = ContactId, TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var sessionManager = new WebChatSessionManager(Options.Create(new WebChatOptions()));
        var sessionId = await sessionManager.ConnectAsync(Tenant, ConvId, new ChannelAddress(ChannelType.WebChat, "webchat-y"), clock);
        var sut = new WebChatInboundRouter(router, switchboard, convStore, contactStore, new PlatformEventBus(), sessionManager);

        await sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);
        await sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);

        await switchboard.Received(1).AssignToQueueAsync(ConvId, Tenant, QueueId, Arg.Any<CancellationToken>());
    }
}
