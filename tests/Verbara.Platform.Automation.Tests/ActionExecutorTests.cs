using System.Net;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Switchboard;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Automation.Tests;

public class ActionExecutorTests
{
    private readonly IConversationSwitchboard _switchboard = Substitute.For<IConversationSwitchboard>();
    private readonly IConversationService _conversationService = Substitute.For<IConversationService>();
    private readonly IConversationLifecycleService _lifecycleService = Substitute.For<IConversationLifecycleService>();
    private readonly ITimerStore _timerStore = Substitute.For<ITimerStore>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId ConvId = EntityId.From("conv-001");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly DefaultActionExecutor _sut;

    public ActionExecutorTests()
    {
        _clock.UtcNow.Returns(Now);
        _sut = new DefaultActionExecutor(
            _switchboard,
            _conversationService,
            _lifecycleService,
            _timerStore,
            _httpClientFactory,
            _clock,
            NullLogger<DefaultActionExecutor>.Instance);
    }

    private static AutomationEvent BuildEvent() =>
        new()
        {
            Trigger = AutomationTrigger.MessageReceived,
            TenantId = Tenant,
            ConversationId = ConvId,
            OccurredAt = Now,
        };

    // --- AssignToQueue ---

    [Fact]
    public async Task ExecuteAsync_ShouldCallSwitchboard_WhenAssignToQueue()
    {
        var action = new AutomationAction
        {
            Type = AutomationActionType.AssignToQueue,
            Config = new Dictionary<string, string> { ["queueId"] = "q-001" },
        };
        _switchboard.AssignToQueueAsync(default, default, default, default)
            .ReturnsForAnyArgs(new OwnershipResult(true, null, ConversationState.Queued, null));

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeTrue();
        await _switchboard.Received(1).AssignToQueueAsync(
            Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFalse_WhenAssignToQueueMissingQueueId()
    {
        var action = new AutomationAction
        {
            Type = AutomationActionType.AssignToQueue,
            Config = new Dictionary<string, string>(),
        };

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeFalse();
    }

    // --- SendMessage ---

    [Fact]
    public async Task ExecuteAsync_ShouldCallConversationService_WhenSendMessage()
    {
        var action = new AutomationAction
        {
            Type = AutomationActionType.SendMessage,
            Config = new Dictionary<string, string> { ["text"] = "Hello from automation" },
        };
        _conversationService.SendMessageAsync(default!, default, default!, default, default, default)
            .ReturnsForAnyArgs(new Message
            {
                MessageId = EntityId.New(),
                ConversationId = ConvId,
                TenantId = Tenant,
                Direction = MessageDirection.System,
                Channel = ChannelType.WebChat,
                Content = new MessageEnvelope([new TextBlock("Hello from automation")]),
                DeliveryStatus = MessageDeliveryStatus.Sent,
                CreatedAt = Now,
            });

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeTrue();
        await _conversationService.Received(1).SendMessageAsync(
            Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<MessageEnvelope>(),
            Arg.Any<EntityId>(), ConversationOwnerKind.System, Arg.Any<CancellationToken>());
    }

    // --- CloseConversation ---

    [Fact]
    public async Task ExecuteAsync_ShouldCallLifecycleService_WhenCloseConversation()
    {
        var action = new AutomationAction
        {
            Type = AutomationActionType.CloseConversation,
            Config = new Dictionary<string, string>(),
        };
        _lifecycleService.CloseAsync(default, default, default).ReturnsForAnyArgs(Task.CompletedTask);

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeTrue();
        await _lifecycleService.Received(1).CloseAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
    }

    // --- ScheduleTimer ---

    [Fact]
    public async Task ExecuteAsync_ShouldSaveTimer_WhenScheduleTimer()
    {
        var action = new AutomationAction
        {
            Type = AutomationActionType.ScheduleTimer,
            Config = new Dictionary<string, string>
            {
                ["callbackRuleId"] = "rule-002",
                ["delaySeconds"] = "300",
            },
        };
        _timerStore.SaveAsync(Arg.Any<ScheduledTimer>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeTrue();
        await _timerStore.Received(1).SaveAsync(
            Arg.Is<ScheduledTimer>(t =>
                t.ConversationId == ConvId &&
                t.FireAt == Now.AddSeconds(300)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFalse_WhenScheduleTimerMissingDelaySeconds()
    {
        var action = new AutomationAction
        {
            Type = AutomationActionType.ScheduleTimer,
            Config = new Dictionary<string, string> { ["callbackRuleId"] = "rule-002" },
        };

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeFalse();
    }

    // --- Webhook ---

    [Fact]
    public async Task ExecuteAsync_ShouldPostToUrl_WhenWebhook()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        var client = new HttpClient(handler);
        _httpClientFactory.CreateClient("AutomationWebhook").Returns(client);

        var action = new AutomationAction
        {
            Type = AutomationActionType.Webhook,
            Config = new Dictionary<string, string> { ["url"] = "https://example.com/hook" },
        };

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeTrue();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFalse_WhenWebhookReturnsBadStatus()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError);
        var client = new HttpClient(handler);
        _httpClientFactory.CreateClient("AutomationWebhook").Returns(client);

        var action = new AutomationAction
        {
            Type = AutomationActionType.Webhook,
            Config = new Dictionary<string, string> { ["url"] = "https://example.com/hook" },
        };

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeFalse();
    }

    // --- SetMetadata / AddTag / SetPriority ---

    [Fact]
    public async Task ExecuteAsync_ShouldReturnTrue_WhenSetMetadata()
    {
        var action = new AutomationAction
        {
            Type = AutomationActionType.SetMetadata,
            Config = new Dictionary<string, string> { ["key"] = "value" },
        };

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeTrue();
    }

    // --- Unknown action type ---

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFalse_WhenUnknownActionType()
    {
        var action = new AutomationAction
        {
            Type = (AutomationActionType)999,
            Config = new Dictionary<string, string>(),
        };

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeFalse();
    }

    // --- EscalateToSupervisor / TransferToBot / SendSurvey (unsupported but succeeds) ---

    [Fact]
    public async Task ExecuteAsync_ShouldReturnTrue_WhenEscalateToSupervisor()
    {
        var action = new AutomationAction
        {
            Type = AutomationActionType.EscalateToSupervisor,
            Config = new Dictionary<string, string>(),
        };

        var result = await _sut.ExecuteAsync(action, BuildEvent(), default);

        result.Should().BeTrue();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        public int CallCount { get; private set; }

        public FakeHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }
}
