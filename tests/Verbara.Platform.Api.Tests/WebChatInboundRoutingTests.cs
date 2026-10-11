using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Unit tests for <see cref="WebChatInboundRouter"/> — the bridge that assigns a WebChat
/// conversation to a queue on the FIRST inbound message of a session. WebChat conversations
/// are pre-created (owner-less, Queued) at session open and the inbound pipeline never routes
/// them, so the auto-distribution worker skips them. This component closes that gap, mirroring
/// the webhook inbound path (<c>WebhookEndpoints.cs:104-108</c>).
/// Since 2.28.0 (spec <c>webchat-visitor-identity</c>) the pipeline resolves a visitor message to the
/// session's own contact (by its <c>webchat-&lt;guid&gt;</c> address) and so to the conversation the
/// session was created with; the fixtures below model exactly that pairing.
/// </summary>
public sealed class WebChatInboundRoutingTests
{
    private static readonly TenantId Tenant = new("t-webchat-001");
    private static readonly EntityId ConvId = EntityId.From("conv-wc-1");
    private static readonly EntityId ContactId = EntityId.From("contact-wc-1");
    private static readonly EntityId QueueId = EntityId.From("q-wc-1");

    private static (WebChatInboundRouter sut, InMemoryConversationStore store, string sessionId, IConversationSwitchboard switchboardMock, PlatformEventBus eventBus)
        BuildWithRealSwitchboard(IReadOnlyDictionary<string, string>? routeMetadata = null)
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

        // The session's contact carries the session's visitor address (WebChatEndpoints.CreateSession).
        var visitorAddress = new ChannelAddress(ChannelType.WebChat, "webchat-x");
        var contact = new Contact { ContactId = ContactId, TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow };
        contact.AddAddress(visitorAddress);
        var contactStore = Substitute.For<IContactStore>();
        contactStore.GetByIdAsync(Tenant, ContactId, Arg.Any<CancellationToken>()).Returns(contact);

        var capacity = Substitute.For<IAgentCapacityService>();
        var eventBus = new PlatformEventBus();
        var switchboard = new ConversationSwitchboard(store, capacity, Substitute.For<IAgentStore>(), clock, eventBus, new FakeAgentAccountStatusLookup());

        var router = Substitute.For<IInboundRouter>();
        router.RouteAsync(default!, default).ReturnsForAnyArgs(
            new RouteResult(QueueId, default, null, routeMetadata));

        var sessionManager = new WebChatSessionManager(Options.Create(new WebChatOptions()));
        var sessionId = sessionManager.ConnectAsync(
            Tenant, ConvId, visitorAddress, clock).GetAwaiter().GetResult();

        var sut = new WebChatInboundRouter(router, switchboard, store, contactStore, eventBus, sessionManager, NullLogger<WebChatInboundRouter>.Instance);
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
    public async Task RouteFirstInboundAsync_ShouldRouteSessionConversation_WhenPipelineReusedSessionContact()
    {
        // D16: the first visitor message lands in the session's conversation, so the conversation routed
        // is the one the session was created with — and no second conversation exists to route.
        var (sut, store, sessionId, _, _) = BuildWithRealSwitchboard();

        await sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);

        var active = await store.FindActiveByContactAsync(Tenant, ContactId, ChannelType.WebChat, CancellationToken.None);
        active.Should().NotBeNull();
        active!.ConversationId.Should().Be(ConvId);
        active.Owner!.Kind.Should().Be(ConversationOwnerKind.Queue);
    }

    [Fact]
    public async Task RouteFirstInboundAsync_ShouldRouteNewConversation_WhenSessionConversationEnded()
    {
        // The session's first message routed its conversation; that conversation later ended, so the
        // pipeline opened a new one for the same contact on the visitor's next message. The new
        // conversation is owner-less like the first one and must be routed, or no agent is ever offered it.
        var (sut, store, sessionId, _, _) = BuildWithRealSwitchboard();
        await sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);

        var reopenedId = EntityId.From("conv-wc-2");
        await store.SaveAsync(new Conversation
        {
            ConversationId = reopenedId,
            TenantId = Tenant,
            ContactId = ContactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        await sut.RouteFirstInboundAsync(
            sessionId, Tenant,
            new PipelineResult(reopenedId, ContactId, EntityId.From("msg-2"), IsNewConversation: true),
            Content(), CancellationToken.None);

        var reopened = await store.GetByIdAsync(Tenant, reopenedId, CancellationToken.None);
        reopened!.Owner.Should().NotBeNull("a new conversation opened by a visitor message is routed to a queue");
        reopened.Owner!.Kind.Should().Be(ConversationOwnerKind.Queue);
        reopened.Owner.OwnerId.Should().Be(QueueId);
    }

    [Fact]
    public async Task RouteFirstInboundAsync_ShouldStampRouteMetadata_WhenPresent()
    {
        // C5 (implicit): WebChat does NOT run the bot, so only the router's metadata (e.g. the
        // ReasonHintMiddleware's "reasonPath") is applied — stamped onto the conversation right
        // after routing and surviving the AssignToQueueAsync reload + save.
        var (sut, store, sessionId, _, _) = BuildWithRealSwitchboard(
            new Dictionary<string, string> { ["reasonPath"] = "support/login" });

        await sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);

        var reloaded = await store.GetByIdAsync(Tenant, ConvId, CancellationToken.None);
        reloaded.Should().NotBeNull();
        reloaded!.Metadata.Should().ContainKey("reasonPath");
        reloaded.Metadata["reasonPath"].Should().Be("support/login");
        reloaded.Owner!.Kind.Should().Be(ConversationOwnerKind.Queue);
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
        var sut = new WebChatInboundRouter(router, switchboard, convStore, contactStore, new PlatformEventBus(), sessionManager, NullLogger<WebChatInboundRouter>.Instance);

        await sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);
        await sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);

        await switchboard.Received(1).AssignToQueueAsync(ConvId, Tenant, QueueId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RouteFirstInboundAsync_ShouldNotThrowAndNotAssign_WhenRouterFindsNoQueue()
    {
        // InboundRouter throws InvalidOperationException when the tenant has no active queue.
        // The visitor's WebSocket must survive: leave the conversation queued+unowned, do not crash.
        var router = Substitute.For<IInboundRouter>();
        router.RouteAsync(default!, default).ThrowsForAnyArgs(new InvalidOperationException("no queue"));
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
        var sessionId = await sessionManager.ConnectAsync(Tenant, ConvId, new ChannelAddress(ChannelType.WebChat, "webchat-z"), clock);
        var sut = new WebChatInboundRouter(router, switchboard, convStore, contactStore, new PlatformEventBus(), sessionManager, NullLogger<WebChatInboundRouter>.Instance);

        var act = () => sut.RouteFirstInboundAsync(sessionId, Tenant, FirstMessageResult(), Content(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        await switchboard.DidNotReceiveWithAnyArgs().AssignToQueueAsync(default, default, default, default);
    }
}
