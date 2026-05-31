using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Switchboard.Tests;

public sealed class ConversationSwitchboardTests : IDisposable
{
    private readonly IConversationStore _store = Substitute.For<IConversationStore>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly PlatformEventBus _eventBus = new();

    private readonly TenantId _tenantId = new("t1");
    private readonly EntityId _conversationId = EntityId.From("conv-001");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private readonly EntityId _queueId = EntityId.From("queue-001");
    private readonly DateTimeOffset _now = new(2026, 3, 21, 12, 0, 0, TimeSpan.Zero);

    private ConversationSwitchboard CreateSut() =>
        new(_store, _capacity, _clock, _eventBus);

    private Conversation BuildConversation(ConversationState state, ConversationOwner? owner = null) =>
        new()
        {
            ConversationId = _conversationId,
            TenantId = _tenantId,
            ContactId = EntityId.From("contact-001"),
            Channel = ChannelType.Voice,
            State = state,
            Owner = owner,
            CreatedAt = _now,
        };

    public ConversationSwitchboardTests()
    {
        _clock.UtcNow.Returns(_now);
    }

    public void Dispose() => _eventBus.Dispose();

    // ─── AssignToQueue ────────────────────────────────────────────────────────

    [Fact]
    public async Task AssignToQueue_ShouldSetQueuedState()
    {
        var conversation = BuildConversation(ConversationState.Queued);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.AssignToQueueAsync(_conversationId, _tenantId, _queueId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewState.Should().Be(ConversationState.Queued);
    }

    [Fact]
    public async Task AssignToQueue_ShouldSetQueueOwner()
    {
        var conversation = BuildConversation(ConversationState.Queued);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.AssignToQueueAsync(_conversationId, _tenantId, _queueId, CancellationToken.None);

        result.NewOwner.Should().NotBeNull();
        result.NewOwner!.Kind.Should().Be(ConversationOwnerKind.Queue);
        result.NewOwner.OwnerId.Should().Be(_queueId);
    }

    // ─── OfferToAgent ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OfferToAgent_ShouldSetOfferedState()
    {
        var conversation = BuildConversation(ConversationState.Queued, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.OfferToAgentAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewState.Should().Be(ConversationState.Offered);
    }

    [Fact]
    public async Task OfferToAgent_ShouldKeepQueueOwner()
    {
        var queueOwner = ConversationOwner.ForQueue(_queueId);
        var conversation = BuildConversation(ConversationState.Queued, queueOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.OfferToAgentAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.NewOwner.Should().Be(queueOwner);
    }

    // ─── AcceptAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptAsync_ShouldSetActiveState()
    {
        var conversation = BuildConversation(ConversationState.Offered, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);
        _capacity.HasCapacityAsync(_tenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>())
                 .Returns(true);

        var sut = CreateSut();
        var result = await sut.AcceptAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewState.Should().Be(ConversationState.Active);
    }

    [Fact]
    public async Task AcceptAsync_ShouldSetAgentOwner()
    {
        var conversation = BuildConversation(ConversationState.Offered, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);
        _capacity.HasCapacityAsync(_tenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>())
                 .Returns(true);

        var sut = CreateSut();
        var result = await sut.AcceptAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.NewOwner!.Kind.Should().Be(ConversationOwnerKind.Agent);
        result.NewOwner.OwnerId.Should().Be(_agentId);
    }

    [Fact]
    public async Task AcceptAsync_ShouldReserveCapacity()
    {
        var conversation = BuildConversation(ConversationState.Offered, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);
        _capacity.HasCapacityAsync(_tenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>())
                 .Returns(true);

        var sut = CreateSut();
        await sut.AcceptAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        await _capacity.Received(1).ReserveAsync(_tenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_ShouldFail_WhenNoCapacity()
    {
        var conversation = BuildConversation(ConversationState.Offered, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);
        _capacity.HasCapacityAsync(_tenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>())
                 .Returns(false);

        var sut = CreateSut();
        var result = await sut.AcceptAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("Agent has no capacity.");
    }

    // ─── RejectAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RejectAsync_ShouldReturnToQueued()
    {
        var queueOwner = ConversationOwner.ForQueue(_queueId);
        var conversation = BuildConversation(ConversationState.Offered, queueOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.RejectAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewState.Should().Be(ConversationState.Queued);
        result.NewOwner.Should().Be(queueOwner);
    }

    [Fact]
    public async Task RejectAsync_ShouldKeepQueueOwner()
    {
        var queueOwner = ConversationOwner.ForQueue(_queueId);
        var conversation = BuildConversation(ConversationState.Offered, queueOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.RejectAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.NewOwner.Should().Be(queueOwner);
    }

    // ─── TransferToQueue ──────────────────────────────────────────────────────

    [Fact]
    public async Task TransferToQueue_ShouldReleaseOldAgentCapacity()
    {
        var agentOwner = ConversationOwner.ForAgent(_agentId);
        var conversation = BuildConversation(ConversationState.Active, agentOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var targetQueue = EntityId.From("queue-002");
        var sut = CreateSut();
        await sut.TransferToQueueAsync(_conversationId, _tenantId, targetQueue, CancellationToken.None);

        await _capacity.Received(1).ReleaseAsync(_tenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransferToQueue_ShouldSetNewQueueOwner()
    {
        var agentOwner = ConversationOwner.ForAgent(_agentId);
        var conversation = BuildConversation(ConversationState.Active, agentOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var targetQueue = EntityId.From("queue-002");
        var sut = CreateSut();
        var result = await sut.TransferToQueueAsync(_conversationId, _tenantId, targetQueue, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewOwner!.Kind.Should().Be(ConversationOwnerKind.Queue);
        result.NewOwner.OwnerId.Should().Be(targetQueue);
        result.NewState.Should().Be(ConversationState.Queued);
    }

    // ─── TransferToAgent ──────────────────────────────────────────────────────

    [Fact]
    public async Task TransferToAgent_ShouldReleaseOldAndReserveNew()
    {
        var oldAgentId = EntityId.From("agent-old");
        var agentOwner = ConversationOwner.ForAgent(oldAgentId);
        var conversation = BuildConversation(ConversationState.Active, agentOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var newAgentId = EntityId.From("agent-new");
        var sut = CreateSut();
        var result = await sut.TransferToAgentAsync(_conversationId, _tenantId, newAgentId, CancellationToken.None);

        result.Success.Should().BeTrue();
        await _capacity.Received(1).ReleaseAsync(_tenantId, oldAgentId, ChannelType.Voice, Arg.Any<CancellationToken>());
        await _capacity.Received(1).ReserveAsync(_tenantId, newAgentId, ChannelType.Voice, Arg.Any<CancellationToken>());
        result.NewOwner!.Kind.Should().Be(ConversationOwnerKind.Agent);
        result.NewOwner.OwnerId.Should().Be(newAgentId);
        result.NewState.Should().Be(ConversationState.Active);
    }

    [Fact]
    public async Task TransferToAgent_ShouldSetNewAgentOwner()
    {
        var conversation = BuildConversation(ConversationState.Active, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var newAgentId = EntityId.From("agent-new");
        var sut = CreateSut();
        var result = await sut.TransferToAgentAsync(_conversationId, _tenantId, newAgentId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewOwner!.Kind.Should().Be(ConversationOwnerKind.Agent);
        result.NewOwner.OwnerId.Should().Be(newAgentId);
    }

    // ─── ReturnToBot ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReturnToBot_ShouldFollowEscalatedPath()
    {
        var botId = EntityId.From("bot-001");
        var agentOwner = ConversationOwner.ForAgent(_agentId);
        var conversation = BuildConversation(ConversationState.Active, agentOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.ReturnToBotAsync(_conversationId, _tenantId, botId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewState.Should().Be(ConversationState.Queued);
        result.NewOwner!.Kind.Should().Be(ConversationOwnerKind.Bot);
        result.NewOwner.OwnerId.Should().Be(botId);
    }

    [Fact]
    public async Task ReturnToBot_ShouldReleaseAgentCapacity()
    {
        var botId = EntityId.From("bot-001");
        var agentOwner = ConversationOwner.ForAgent(_agentId);
        var conversation = BuildConversation(ConversationState.Active, agentOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        await sut.ReturnToBotAsync(_conversationId, _tenantId, botId, CancellationToken.None);

        await _capacity.Received(1).ReleaseAsync(_tenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>());
    }

    // ─── Hold ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hold_ShouldTransitionToOnHold_WhenActive()
    {
        var conversation = BuildConversation(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.HoldAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewState.Should().Be(ConversationState.OnHold);
    }

    [Fact]
    public async Task Hold_ShouldFail_WhenNotOwnedByAgent()
    {
        var conversation = BuildConversation(ConversationState.Active, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.HoldAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Hold_ShouldFail_WhenNotActive()
    {
        var conversation = BuildConversation(ConversationState.Queued, ConversationOwner.ForAgent(_agentId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.HoldAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    // ─── Unhold ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unhold_ShouldTransitionToActive_WhenOnHold()
    {
        var conversation = BuildConversation(ConversationState.OnHold, ConversationOwner.ForAgent(_agentId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.UnholdAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewState.Should().Be(ConversationState.Active);
    }

    [Fact]
    public async Task Unhold_ShouldFail_WhenNotOnHold()
    {
        var conversation = BuildConversation(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.UnholdAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    // ─── Error cases ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConversationNotFound_ShouldFail_OnAssignToQueue()
    {
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns((Conversation?)null);

        var sut = CreateSut();
        var result = await sut.AssignToQueueAsync(_conversationId, _tenantId, _queueId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("Conversation not found.");
    }

    [Fact]
    public async Task ConversationNotFound_ShouldFail_OnAccept()
    {
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns((Conversation?)null);

        var sut = CreateSut();
        var result = await sut.AcceptAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("Conversation not found.");
    }

    [Fact]
    public async Task InvalidTransition_ShouldFail_OnOffer()
    {
        // Cannot offer from Active state
        var conversation = BuildConversation(ConversationState.Active);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.OfferToAgentAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("Active");
    }

    [Fact]
    public async Task InvalidTransition_ShouldFail_OnReject_WhenNotOffered()
    {
        // Cannot reject from Active state (no Active→Queued transition)
        var conversation = BuildConversation(ConversationState.Active);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        var result = await sut.RejectAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("Active");
    }

    [Fact]
    public async Task AcceptAsync_ShouldNotReserveCapacity_WhenNoCapacity()
    {
        var conversation = BuildConversation(ConversationState.Offered, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);
        _capacity.HasCapacityAsync(_tenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>())
                 .Returns(false);

        var sut = CreateSut();
        await sut.AcceptAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        await _capacity.DidNotReceive().ReserveAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignToQueue_ShouldSaveConversation()
    {
        var conversation = BuildConversation(ConversationState.Queued);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var sut = CreateSut();
        await sut.AssignToQueueAsync(_conversationId, _tenantId, _queueId, CancellationToken.None);

        await _store.Received(1).SaveAsync(conversation, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransferToQueue_ShouldNotReleaseCapacity_WhenOwnedByQueue()
    {
        var queueOwner = ConversationOwner.ForQueue(_queueId);
        var conversation = BuildConversation(ConversationState.Active, queueOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var targetQueue = EntityId.From("queue-002");
        var sut = CreateSut();
        await sut.TransferToQueueAsync(_conversationId, _tenantId, targetQueue, CancellationToken.None);

        await _capacity.DidNotReceive().ReleaseAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    // ─── Event publishing ─────────────────────────────────────────────────────

    [Fact]
    public async Task AssignToQueueAsync_ShouldPublishStateChangedEvent_WhenSuccessful()
    {
        var conversation = BuildConversation(ConversationState.Queued);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var events = new List<PlatformEvent>();
        using var sub = _eventBus.Events.Subscribe(e => events.Add(e));

        var sut = CreateSut();
        await sut.AssignToQueueAsync(_conversationId, _tenantId, _queueId, CancellationToken.None);

        events.Should().ContainSingle(e => e is ConversationStateChangedEvent);
        var stateEvent = (ConversationStateChangedEvent)events.Single(e => e is ConversationStateChangedEvent);
        stateEvent.TenantId.Should().Be(_tenantId.Value);
        stateEvent.ConversationId.Should().Be(_conversationId.Value);
        stateEvent.NewState.Should().Be("Queued");
    }

    [Fact]
    public async Task OfferToAgentAsync_ShouldPublishOfferedAndStateChangedEvents_WhenSuccessful()
    {
        var conversation = BuildConversation(ConversationState.Queued, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var events = new List<PlatformEvent>();
        using var sub = _eventBus.Events.Subscribe(e => events.Add(e));

        var sut = CreateSut();
        await sut.OfferToAgentAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        events.Should().Contain(e => e is ConversationOfferedEvent);
        events.Should().Contain(e => e is ConversationStateChangedEvent);

        var offeredEvent = (ConversationOfferedEvent)events.Single(e => e is ConversationOfferedEvent);
        offeredEvent.TenantId.Should().Be(_tenantId.Value);
        offeredEvent.ConversationId.Should().Be(_conversationId.Value);
        offeredEvent.AgentId.Should().Be(_agentId.Value);
        // The agent UI renders the offered card from this payload (an Offered conversation keeps
        // Owner=Queue, so the agentId-filtered inbox query won't surface it) — carry the channel.
        offeredEvent.Channel.Should().Be(conversation.Channel.ToString());

        var stateEvent = (ConversationStateChangedEvent)events.Single(e => e is ConversationStateChangedEvent);
        stateEvent.OldState.Should().Be("Queued");
        stateEvent.NewState.Should().Be("Offered");
    }

    [Fact]
    public async Task AcceptAsync_ShouldPublishAssignedAndStateChangedEvents_WhenSuccessful()
    {
        var conversation = BuildConversation(ConversationState.Offered, ConversationOwner.ForQueue(_queueId));
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);
        _capacity.HasCapacityAsync(_tenantId, _agentId, ChannelType.Voice, Arg.Any<CancellationToken>())
                 .Returns(true);

        var events = new List<PlatformEvent>();
        using var sub = _eventBus.Events.Subscribe(e => events.Add(e));

        var sut = CreateSut();
        await sut.AcceptAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        events.Should().Contain(e => e is ConversationAssignedEvent);
        events.Should().Contain(e => e is ConversationStateChangedEvent);

        var assignedEvent = (ConversationAssignedEvent)events.Single(e => e is ConversationAssignedEvent);
        assignedEvent.TenantId.Should().Be(_tenantId.Value);
        assignedEvent.ConversationId.Should().Be(_conversationId.Value);
        assignedEvent.AgentId.Should().Be(_agentId.Value);

        var stateEvent = (ConversationStateChangedEvent)events.Single(e => e is ConversationStateChangedEvent);
        stateEvent.OldState.Should().Be("Offered");
        stateEvent.NewState.Should().Be("Active");
    }

    [Fact]
    public async Task RejectAsync_ShouldPublishStateChangedEvent_WhenSuccessful()
    {
        var queueOwner = ConversationOwner.ForQueue(_queueId);
        var conversation = BuildConversation(ConversationState.Offered, queueOwner);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(conversation);

        var events = new List<PlatformEvent>();
        using var sub = _eventBus.Events.Subscribe(e => events.Add(e));

        var sut = CreateSut();
        await sut.RejectAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

        events.Should().ContainSingle(e => e is ConversationStateChangedEvent);
        var stateEvent = (ConversationStateChangedEvent)events.Single(e => e is ConversationStateChangedEvent);
        stateEvent.OldState.Should().Be("Offered");
        stateEvent.NewState.Should().Be("Queued");
    }
}
