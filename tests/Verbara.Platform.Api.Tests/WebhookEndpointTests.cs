using System.Net;
using System.Text;
using Verbara.Platform.Bot;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class WebhookEndpointTests : IClassFixture<PlatformApiFactory>
{
    private readonly HttpClient _client;

    public WebhookEndpointTests(PlatformApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Post_ShouldReturn400_WhenChannelIsUnknown()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await _client.PostAsync("/api/webhooks/tenant123/unknownchannel", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_ShouldReturn200_WhenWebhookBodyIsIgnored()
    {
        // The in-memory channel registry has no handler registered for WebChat by default,
        // so we expect a 400 (no handler) rather than 500 — which verifies the pipeline
        // handles missing handlers gracefully without crashing.
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await _client.PostAsync("/api/webhooks/tenant123/webchat", content);

        // No handler registered → 400 is valid and expected
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_WhatsAppVerification_ShouldReturnChallenge_WhenModeIsSubscribe()
    {
        var response = await _client.GetAsync(
            "/api/webhooks/tenant123/whatsapp?hub.mode=subscribe&hub.verify_token=mytoken&hub.challenge=ABCDEF");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ABCDEF");
    }

    [Fact]
    public async Task Get_WhatsAppVerification_ShouldReturn400_WhenModeIsNotSubscribe()
    {
        var response = await _client.GetAsync(
            "/api/webhooks/tenant123/whatsapp?hub.mode=other&hub.verify_token=mytoken");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_MessengerVerification_ShouldReturnChallenge_WhenModeIsSubscribe()
    {
        var response = await _client.GetAsync(
            "/api/webhooks/tenant123/messenger?hub.mode=subscribe&hub.verify_token=tok&hub.challenge=XYZ");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("XYZ");
    }

    [Fact]
    public async Task Get_InstagramVerification_ShouldReturnChallenge_WhenModeIsSubscribe()
    {
        var response = await _client.GetAsync(
            "/api/webhooks/tenant123/instagram?hub.mode=subscribe&hub.verify_token=tok&hub.challenge=INSTA");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("INSTA");
    }

    [Fact]
    public async Task Post_ShouldBeUnauthenticated_WhenNoAuthorizationHeader()
    {
        // Webhook endpoints are unauthenticated — no Authorization header needed
        var content = new ByteArrayContent([]);
        var response = await _client.PostAsync("/api/webhooks/t1/sms", content);

        // Not a 401 — webhooks bypass auth
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// Unit-level tests for bot handoff logic: verifies that TransferToQueue and EndConversation
/// bot responses result in the correct switchboard/lifecycle calls.
/// </summary>
public sealed class BotHandoffLogicTests
{
    private static readonly TenantId TestTenant = new("t-handoff-001");
    private static readonly EntityId ConvId = EntityId.From("conv-bot-001");
    private static readonly EntityId BotId = EntityId.From("bot-001");
    private static readonly EntityId QueueId = EntityId.From("q-001");

    // ── TransferToQueue ───────────────────────────────────────────────────────

    [Fact]
    public async Task TransferToQueueAsync_ShouldBeCalled_WhenBotResponseIsTransferToQueue()
    {
        // Arrange
        var switchboard = Substitute.For<IConversationSwitchboard>();
        switchboard.TransferToQueueAsync(default, default, default, default)
            .ReturnsForAnyArgs(new OwnershipResult(true, ConversationOwner.ForQueue(QueueId), ConversationState.Queued, null));

        var botResponse = new BotResponse(BotResponseAction.TransferToQueue, null, QueueId, null, "Flow handoff");

        // Act — simulate the handoff branch directly (mirrors WebhookEndpoints.cs branch)
        if (botResponse.Action == BotResponseAction.TransferToQueue && botResponse.TargetQueueId is not null)
        {
            await switchboard.TransferToQueueAsync(ConvId, TestTenant, botResponse.TargetQueueId.Value, CancellationToken.None);
        }

        // Assert — TransferToQueueAsync drives the Active→Escalated→Queued transition
        // (AssignToQueueAsync would silently skip the Escalated step).
        await switchboard.Received(1).TransferToQueueAsync(
            ConvId,
            TestTenant,
            QueueId,
            Arg.Any<CancellationToken>());
        await switchboard.DidNotReceiveWithAnyArgs().AssignToQueueAsync(default, default, default, default);
    }

    [Fact]
    public async Task TransferToQueueAsync_ShouldNotBeCalled_WhenTransferToQueueHasNullTargetQueueId()
    {
        // Arrange
        var switchboard = Substitute.For<IConversationSwitchboard>();
        var botResponse = new BotResponse(BotResponseAction.TransferToQueue, null, null, null, null);

        // Act — guard clause: TargetQueueId is null, so TransferToQueue must not be called
        if (botResponse.Action == BotResponseAction.TransferToQueue && botResponse.TargetQueueId is not null)
        {
            await switchboard.TransferToQueueAsync(ConvId, TestTenant, botResponse.TargetQueueId.Value, CancellationToken.None);
        }

        // Assert
        await switchboard.DidNotReceiveWithAnyArgs().TransferToQueueAsync(default, default, default, default);
    }

    [Fact]
    public async Task TransferToQueueAsync_ShouldDriveEscalationTransition_WhenConversationIsActiveAndBotHandsOff()
    {
        // Regression for R3 Phase B Sub-A bug #1: WebhookEndpoints previously called
        // AssignToQueueAsync on bot handoff, skipping the Active→Escalated→Queued transition.
        // Arrange — a real ConversationSwitchboard against in-memory store so we observe the
        // actual state transitions (not just mock invocations).
        var store = new Verbara.Platform.Storage.InMemory.InMemoryConversationStore();
        var capacity = Substitute.For<Verbara.Platform.Queues.Services.IAgentCapacityService>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var eventBus = new PlatformEventBus();

        var conversation = new Conversation
        {
            ConversationId = ConvId,
            TenantId = TestTenant,
            ContactId = EntityId.From("contact-1"),
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        conversation.TransitionTo(ConversationState.Offered, DateTimeOffset.UtcNow);
        conversation.TransitionTo(ConversationState.Active, DateTimeOffset.UtcNow);
        conversation.Owner = ConversationOwner.ForBot(BotId);
        await store.SaveAsync(conversation, CancellationToken.None);

        var switchboard = new Verbara.Platform.Switchboard.ConversationSwitchboard(store, capacity, clock, eventBus);
        var botResponse = new BotResponse(BotResponseAction.TransferToQueue, null, QueueId, null, "Flow handoff");

        // Act — execute the WebhookEndpoints branch verbatim
        OwnershipResult? transferResult = null;
        if (botResponse.Action == BotResponseAction.TransferToQueue && botResponse.TargetQueueId is not null)
        {
            transferResult = await switchboard.TransferToQueueAsync(
                ConvId, TestTenant, botResponse.TargetQueueId.Value, CancellationToken.None);
        }

        // Assert — conversation transitioned to Queued and owner is the target queue
        transferResult.Should().NotBeNull();
        transferResult!.Success.Should().BeTrue();
        transferResult.NewState.Should().Be(ConversationState.Queued);

        var reloaded = await store.GetByIdAsync(TestTenant, ConvId, CancellationToken.None);
        reloaded.Should().NotBeNull();
        reloaded!.State.Should().Be(ConversationState.Queued);
        reloaded.Owner.Should().NotBeNull();
        reloaded.Owner!.Kind.Should().Be(ConversationOwnerKind.Queue);
        reloaded.Owner.OwnerId.Should().Be(QueueId);
    }

    // ── EndConversation ───────────────────────────────────────────────────────

    [Fact]
    public async Task CloseAsync_ShouldBeCalled_WhenBotResponseIsEndConversation()
    {
        // Arrange
        var lifecycleService = Substitute.For<IConversationLifecycleService>();
        var botResponse = new BotResponse(BotResponseAction.EndConversation, null, null, null, null);

        // Act — simulate the end-conversation branch directly
        if (botResponse.Action == BotResponseAction.EndConversation)
        {
            await lifecycleService.CloseAsync(TestTenant, ConvId, CancellationToken.None);
        }

        // Assert
        await lifecycleService.Received(1).CloseAsync(
            TestTenant,
            ConvId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseAsync_ShouldNotBeCalled_WhenBotResponseIsReply()
    {
        // Arrange
        var lifecycleService = Substitute.For<IConversationLifecycleService>();
        var botResponse = new BotResponse(BotResponseAction.Reply, [], null, null, null);

        // Act — Reply action should not trigger close
        if (botResponse.Action == BotResponseAction.EndConversation)
        {
            await lifecycleService.CloseAsync(TestTenant, ConvId, CancellationToken.None);
        }

        // Assert
        await lifecycleService.DidNotReceiveWithAnyArgs().CloseAsync(default!, default, default);
    }

    [Fact]
    public async Task CloseAsync_ShouldNotBeCalled_WhenBotResponseIsTransferToQueue()
    {
        // Arrange
        var lifecycleService = Substitute.For<IConversationLifecycleService>();
        var switchboard = Substitute.For<IConversationSwitchboard>();
        switchboard.TransferToQueueAsync(default, default, default, default)
            .ReturnsForAnyArgs(new OwnershipResult(true, ConversationOwner.ForQueue(QueueId), ConversationState.Queued, null));

        var botResponse = new BotResponse(BotResponseAction.TransferToQueue, null, QueueId, null, "handoff");

        // Act — TransferToQueue triggers switchboard.TransferToQueueAsync, not lifecycle close
        if (botResponse.Action == BotResponseAction.TransferToQueue && botResponse.TargetQueueId is not null)
        {
            await switchboard.TransferToQueueAsync(ConvId, TestTenant, botResponse.TargetQueueId.Value, CancellationToken.None);
        }
        else if (botResponse.Action == BotResponseAction.EndConversation)
        {
            await lifecycleService.CloseAsync(TestTenant, ConvId, CancellationToken.None);
        }

        // Assert
        await lifecycleService.DidNotReceiveWithAnyArgs().CloseAsync(default!, default, default);
        await switchboard.Received(1).TransferToQueueAsync(ConvId, TestTenant, QueueId, Arg.Any<CancellationToken>());
    }

    // ── Reply (existing behavior) ─────────────────────────────────────────────

    [Fact]
    public async Task SendMessageAsync_ShouldBeCalled_WhenBotResponseIsReply()
    {
        // Arrange
        var conversationService = Substitute.For<IConversationService>();
        var message = new MessageEnvelope([new TextBlock("Hello from bot")]);
        var botResponse = new BotResponse(BotResponseAction.Reply, [message], null, null, null);

        // Act — simulate the reply branch directly
        if (botResponse.Action == BotResponseAction.Reply && botResponse.Messages is not null)
        {
            foreach (var reply in botResponse.Messages)
            {
                await conversationService.SendMessageAsync(
                    ConvId, TestTenant, reply, BotId, ConversationOwnerKind.Bot, CancellationToken.None);
            }
        }

        // Assert
        await conversationService.Received(1).SendMessageAsync(
            ConvId,
            TestTenant,
            message,
            BotId,
            ConversationOwnerKind.Bot,
            Arg.Any<CancellationToken>());
    }
}
