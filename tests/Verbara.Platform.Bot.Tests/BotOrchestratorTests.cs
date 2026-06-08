using Verbara.Platform.Bot;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Bot.Tests;

public class BotOrchestratorTests : IDisposable
{
    private readonly IBotConfigStore _configStore = Substitute.For<IBotConfigStore>();
    private readonly IFlowExecutionEngine _flowEngine = Substitute.For<IFlowExecutionEngine>();
    private readonly BotAnalyticsCollector _analytics = new();

    public void Dispose()
    {
        _analytics.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly TenantId _tenantId = new("t1");
    private readonly EntityId _conversationId = EntityId.From("conv-001");
    private readonly EntityId _botId = EntityId.From("bot-001");
    private readonly EntityId _flowId = EntityId.From("flow-001");
    private readonly EntityId _queueId = EntityId.From("queue-001");
    private readonly EntityId _executionId = EntityId.From("exec-001");
    private readonly MessageEnvelope _message = new([]);

    private BotConfiguration BuildConfig(bool isActive = true, int maxTurns = 20) =>
        new()
        {
            BotId = _botId,
            TenantId = _tenantId,
            Name = "Test Bot",
            DefaultFlowId = _flowId,
            FallbackQueueId = _queueId,
            IsActive = isActive,
            MaxTurnsBeforeHandoff = maxTurns,
        };

    private FlowExecution BuildExecution() =>
        new()
        {
            ExecutionId = _executionId,
            FlowId = _flowId,
            FlowVersion = 1,
            TenantId = _tenantId,
            ConversationId = _conversationId,
            CurrentNodeId = EntityId.From("node-001"),
            StartedAt = DateTimeOffset.UtcNow,
        };

    private BotOrchestrator CreateSut() =>
        new(_configStore, _flowEngine, _analytics, NullLogger<BotOrchestrator>.Instance);

    // ── no config / inactive ─────────────────────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldEndConversation_WhenNoBotConfig()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns((BotConfiguration?)null);

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.EndConversation);
    }

    [Fact]
    public async Task ProcessMessage_ShouldEndConversation_WhenBotIsInactive()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig(isActive: false));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.EndConversation);
        response.HandoffReason.Should().Be("Bot is inactive.");
    }

    // ── first message starts a new execution ─────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldStartFlowExecution_WhenFirstMessage()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns((FlowExecution?)null);
        _flowEngine.StartAsync(_tenantId, _flowId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.WaitingForInput, null, null, null));

        var sut = CreateSut();
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        await _flowEngine.Received(1).StartAsync(_tenantId, _flowId, _conversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessMessage_ShouldNotStartNewExecution_WhenExecutionAlreadyActive()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.WaitingForInput, null, null, null));

        var sut = CreateSut();
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        await _flowEngine.DidNotReceive().StartAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
    }

    // ── reply path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldReturnReply_WhenFlowProducesMessages()
    {
        var outbound = new List<MessageEnvelope> { new([]) };
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.WaitingForInput, outbound, null, null));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.Reply);
        response.Messages.Should().HaveCount(1);
    }

    [Fact]
    public async Task ProcessMessage_ShouldReturnReply_WhenRunning()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.Running, null, null, null));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.Reply);
    }

    // ── handoff path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldTransferToQueue_WhenFlowHandsOff()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.HandedOff, null, _queueId, MessagePriority.Normal));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.TransferToQueue);
        response.TargetQueueId.Should().Be(_queueId);
        response.Priority.Should().Be(MessagePriority.Normal);
    }

    [Fact]
    public async Task ProcessMessage_ShouldUseFallbackQueue_WhenFlowHandoffHasNoQueue()
    {
        var fallbackQueueId = EntityId.From("fallback-queue");
        var config = new BotConfiguration
        {
            BotId = _botId,
            TenantId = _tenantId,
            Name = "Test Bot",
            DefaultFlowId = _flowId,
            FallbackQueueId = fallbackQueueId,
        };
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(config);
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.HandedOff, null, null, null));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.TransferToQueue);
        response.TargetQueueId.Should().Be(fallbackQueueId);
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldCopyFlowMetadataIntoBotResponse_WhenHandoff()
    {
        var metadata = new Dictionary<string, string> { ["reason"] = "billing", ["topic"] = "refund" };
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.HandedOff, null, _queueId, MessagePriority.Normal, metadata));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.TransferToQueue);
        response.FlowMetadata.Should().NotBeNull();
        response.FlowMetadata!.Should().ContainKey("reason").WhoseValue.Should().Be("billing");
        response.FlowMetadata.Should().ContainKey("topic").WhoseValue.Should().Be("refund");
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldCopyFlowMetadataIntoBotResponse_WhenReply()
    {
        var metadata = new Dictionary<string, string> { ["answer"] = "yes" };
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.WaitingForInput, null, null, null, metadata));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.Reply);
        response.FlowMetadata.Should().NotBeNull();
        response.FlowMetadata!.Should().ContainKey("answer").WhoseValue.Should().Be("yes");
    }

    // ── completion path ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldEndConversation_WhenFlowCompletes()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.Completed, null, null, null));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.EndConversation);
    }

    [Fact]
    public async Task ProcessMessage_ShouldEndConversation_WhenFlowFails()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.Failed, null, null, null));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.EndConversation);
    }

    // ── max turns ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldForceHandoff_WhenMaxTurnsExceeded()
    {
        var config = BuildConfig(maxTurns: 1);
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(config);
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.WaitingForInput, null, null, null));

        var sut = CreateSut();

        // First message (turn 1) — within limit
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        // Second message (turn 2) — exceeds maxTurns = 1
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.TransferToQueue);
        response.HandoffReason.Should().Be("Max turns exceeded");
    }

    [Fact]
    public async Task ProcessMessage_ShouldUseFallbackQueue_WhenMaxTurnsForced()
    {
        var config = BuildConfig(maxTurns: 0);
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(config);

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.TransferToQueue);
        response.TargetQueueId.Should().Be(_queueId);
    }

    // ── analytics events ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldEmitConversationStarted_OnFirstMessage()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns((FlowExecution?)null);
        _flowEngine.StartAsync(_tenantId, _flowId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.WaitingForInput, null, null, null));

        BotAnalyticsEvent? emitted = null;
        _analytics.Events.Subscribe(e => emitted = e);

        var sut = CreateSut();
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        emitted.Should().NotBeNull();
        emitted!.Type.Should().Be(BotAnalyticsEventType.ConversationStarted);
    }

    [Fact]
    public async Task ProcessMessage_ShouldEmitHandedOff_WhenFlowHandsOff()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.HandedOff, null, _queueId, null));

        BotAnalyticsEvent? emitted = null;
        _analytics.Events.Subscribe(e => emitted = e);

        var sut = CreateSut();
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        emitted!.Type.Should().Be(BotAnalyticsEventType.HandedOff);
    }

    [Fact]
    public async Task ProcessMessage_ShouldEmitResolved_WhenFlowCompletes()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.Completed, null, null, null));

        BotAnalyticsEvent? emitted = null;
        _analytics.Events.Subscribe(e => emitted = e);

        var sut = CreateSut();
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        emitted!.Type.Should().Be(BotAnalyticsEventType.Resolved);
    }

    [Fact]
    public async Task ProcessMessage_ShouldEmitMaxTurnsReached_WhenMaxExceeded()
    {
        var config = BuildConfig(maxTurns: 0);
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(config);

        var events = new List<BotAnalyticsEvent>();
        _analytics.Events.Subscribe(e => events.Add(e));

        var sut = CreateSut();
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        events.Should().ContainSingle(e => e.Type == BotAnalyticsEventType.MaxTurnsReached);
    }

    // ── flow engine exception path ────────────────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldEndConversation_WhenFlowEngineThrows()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns<FlowStepResult>(_ => throw new InvalidOperationException("Engine error"));

        var sut = CreateSut();
        var response = await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.EndConversation);
        response.HandoffReason.Should().Be("Flow execution failed.");
    }

    [Fact]
    public async Task ProcessMessage_ShouldEmitFailed_WhenFlowEngineThrows()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig());
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns<FlowStepResult>(_ => throw new InvalidOperationException("Engine error"));

        BotAnalyticsEvent? emitted = null;
        _analytics.Events.Subscribe(e => emitted = e);

        var sut = CreateSut();
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        emitted!.Type.Should().Be(BotAnalyticsEventType.Failed);
    }

    // ── turn counting ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldTrackTurnCountAcrossMessages()
    {
        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig(maxTurns: 5));
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.ProcessMessageAsync(_executionId, _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.WaitingForInput, null, null, null));

        var turnCounts = new List<int?>();
        _analytics.Events.Subscribe(e => turnCounts.Add(e.TurnCount));

        // First message starts new execution, emits ConversationStarted.
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns((FlowExecution?)null);
        _flowEngine.StartAsync(_tenantId, _flowId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());

        var sut = CreateSut();
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        turnCounts.Should().Contain(1);
    }

    // ── independent conversations ─────────────────────────────────────────────

    [Fact]
    public async Task ProcessMessage_ShouldTrackTurnsIndependently_PerConversation()
    {
        var conv2 = EntityId.From("conv-002");
        var exec2 = new FlowExecution
        {
            ExecutionId = EntityId.From("exec-002"),
            FlowId = _flowId,
            FlowVersion = 1,
            TenantId = _tenantId,
            ConversationId = conv2,
            CurrentNodeId = EntityId.From("node-001"),
            StartedAt = DateTimeOffset.UtcNow,
        };

        _configStore.GetDefaultAsync(_tenantId, Arg.Any<CancellationToken>())
                    .Returns(BuildConfig(maxTurns: 1));
        _flowEngine.GetActiveExecutionAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
                   .Returns(BuildExecution());
        _flowEngine.GetActiveExecutionAsync(_tenantId, conv2, Arg.Any<CancellationToken>())
                   .Returns(exec2);
        _flowEngine.ProcessMessageAsync(Arg.Any<EntityId>(), _message, Arg.Any<CancellationToken>())
                   .Returns(new FlowStepResult(FlowExecutionStatus.WaitingForInput, null, null, null));

        var sut = CreateSut();

        // First conversation uses its first turn.
        await sut.ProcessMessageAsync(_conversationId, _tenantId, _message, CancellationToken.None);

        // Second conversation should still be on turn 1 (within limit).
        var response = await sut.ProcessMessageAsync(conv2, _tenantId, _message, CancellationToken.None);

        response.Action.Should().Be(BotResponseAction.Reply);
    }
}
