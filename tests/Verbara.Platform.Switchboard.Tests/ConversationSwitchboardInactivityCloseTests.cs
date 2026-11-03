using Microsoft.Extensions.Logging;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// conversations-close-after-inactivity task 2.1 (design D3, D4, D5): <see cref="ConversationSwitchboard.CloseForInactivityAsync"/>
/// closes through the store's conditional update on the state, owner and clock the pass read; only the winner releases
/// the read owner's unit (agent-held states only) and publishes <c>read state → Closed</c> once.
/// </summary>
public sealed class ConversationSwitchboardInactivityCloseTests : IDisposable
{
    private readonly IConversationStore _store = Substitute.For<IConversationStore>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly FakeAgentAccountStatusLookup _accountStatus = new();
    private readonly RecordingLogger _logger = new();

    private readonly TenantId _tenantId = new("t1");
    private readonly EntityId _conversationId = EntityId.From("conv-idle");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private readonly DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    // The store's row, as the in-memory and Postgres stores hold it; the fake conditional update compares against it.
    private ConversationState _rowState;
    private ConversationOwner? _rowOwner;
    private DateTimeOffset _rowClock;
    private bool _rowClosed;

    public ConversationSwitchboardInactivityCloseTests()
    {
        _clock.UtcNow.Returns(_now);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(_ => new Conversation
              {
                  ConversationId = _conversationId,
                  TenantId = _tenantId,
                  ContactId = EntityId.From("contact-001"),
                  Channel = ChannelType.WebChat,
                  State = _rowState,
                  Owner = _rowOwner,
                  CreatedAt = _now.AddDays(-5),
                  LastMessageAt = _rowClock,
              });
        // A faithful fake of TryCloseInactiveAsync: wins once, and only while state, owner and clock still match the read.
        _store.TryCloseInactiveAsync(_tenantId, Arg.Any<InactivityCloseRequest>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  var r = call.Arg<InactivityCloseRequest>();
                  lock (_store)
                  {
                      if (_rowClosed
                          || !ConversationStateMachine.CanCloseForInactivity(r.ReadState)
                          || _rowState != r.ReadState
                          || _rowOwner?.Kind != r.ReadOwner?.Kind
                          || _rowOwner?.OwnerId != r.ReadOwner?.OwnerId
                          || _rowClock >= r.Cutoff)
                      {
                          return Task.FromResult<InactivityCloseOutcome?>(null);
                      }

                      _rowClosed = true;
                      _rowState = ConversationState.Closed;
                      var reason = _rowClock < r.BacklogCutoff
                          ? ConversationCloseReasons.InactivityBacklog
                          : ConversationCloseReasons.Inactivity;
                      return Task.FromResult<InactivityCloseOutcome?>(new InactivityCloseOutcome(reason, _rowClock));
                  }
              });
    }

    public void Dispose() => _eventBus.Dispose();

    private ConversationSwitchboard CreateSut() =>
        new(_store, _capacity, _agents, _clock, _eventBus, _accountStatus, logger: _logger);

    private void RowIs(ConversationState state, ConversationOwner? owner, double idleHours = 73)
    {
        _rowState = state;
        _rowOwner = owner;
        _rowClock = _now.AddHours(-idleHours);
        _rowClosed = false;
    }

    // What the pass read: the row's state, owner and clock now, with a 72 h window and a 1 h grace.
    private InactivityCloseRequest ReadRow() =>
        new(_conversationId, _rowState, _rowOwner, _now.AddHours(-72), _now.AddHours(-73.5), _now);

    private List<ConversationStateChangedEvent> RecordStateChanges(out IDisposable subscription)
    {
        var events = new List<ConversationStateChangedEvent>();
        subscription = _eventBus.Events.Subscribe(e =>
        {
            if (e is ConversationStateChangedEvent changed)
                lock (events)
                    events.Add(changed);
        });
        return events;
    }

    [Theory]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.OnHold)]
    [InlineData(ConversationState.Consulting)]
    public async Task CloseForInactivityAsync_ShouldReleaseOneUnitAndPublishOnce_WhenAnAgentHeldChatCloses(ConversationState state)
    {
        // D3: Active/OnHold/Consulting → Closed is not in the transition table; the close works without it.
        ConversationStateMachine.CanTransition(state, ConversationState.Closed).Should().BeFalse();
        RowIs(state, ConversationOwner.ForAgent(_agentId));
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;

        var outcome = await CreateSut().CloseForInactivityAsync(_tenantId, ReadRow(), ChannelType.WebChat, CancellationToken.None);

        outcome.Should().NotBeNull("the conditional close won");
        outcome!.Reason.Should().Be(ConversationCloseReasons.Inactivity);
        _rowState.Should().Be(ConversationState.Closed);
        await _capacity.Received(1).ReleaseAsync(_tenantId, _agentId, ChannelType.WebChat, Arg.Any<CancellationToken>());
        _capacity.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAgentCapacityService.ReleaseAsync))
                 .Should().Be(1, "the owner's unit is released exactly once");
        events.Should().ContainSingle()
              .Which.Should().Match<ConversationStateChangedEvent>(e =>
                  e.TenantId == _tenantId.Value && e.ConversationId == _conversationId.Value
                  && e.OldState == state.ToString() && e.NewState == nameof(ConversationState.Closed));
    }

    [Theory]
    [InlineData(ConversationState.WaitingForCustomer)]
    [InlineData(ConversationState.Resolved)]
    public async Task CloseForInactivityAsync_ShouldCloseWithoutReleasing_WhenTheChatIsParked(ConversationState state)
    {
        // A parked conversation keeps its agent owner but holds no unit (the reconcile counts Active only).
        RowIs(state, ConversationOwner.ForAgent(_agentId));
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;

        var outcome = await CreateSut().CloseForInactivityAsync(_tenantId, ReadRow(), ChannelType.WebChat, CancellationToken.None);

        outcome.Should().NotBeNull();
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
        events.Should().ContainSingle()
              .Which.Should().Match<ConversationStateChangedEvent>(e =>
                  e.TenantId == _tenantId.Value && e.ConversationId == _conversationId.Value
                  && e.OldState == state.ToString() && e.NewState == nameof(ConversationState.Closed));
    }

    [Fact]
    public async Task CloseForInactivityAsync_ShouldReturnTheBacklogReason_WhenTheStoreDecidesBacklog()
    {
        RowIs(ConversationState.Active, ConversationOwner.ForAgent(_agentId), idleHours: 200);

        var outcome = await CreateSut().CloseForInactivityAsync(_tenantId, ReadRow(), ChannelType.WebChat, CancellationToken.None);

        outcome.Should().NotBeNull();
        outcome!.Reason.Should().Be(ConversationCloseReasons.InactivityBacklog, "the reason is the statement's, passed through (D12)");
    }

    [Fact]
    public async Task CloseForInactivityAsync_ShouldReleaseAndPublishNothing_WhenTheConditionalUpdateLoses()
    {
        // Two sweeps read the same idle Active chat; the store lets exactly one through.
        RowIs(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        var read = ReadRow();
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;
        var sut = CreateSut();

        var outcomes = await Task.WhenAll(
            sut.CloseForInactivityAsync(_tenantId, read, ChannelType.WebChat, CancellationToken.None),
            sut.CloseForInactivityAsync(_tenantId, read, ChannelType.WebChat, CancellationToken.None));

        outcomes.Count(o => o is not null).Should().Be(1, "only the caller whose update won closed it");
        await _capacity.Received(1).ReleaseAsync(_tenantId, _agentId, ChannelType.WebChat, Arg.Any<CancellationToken>());
        events.Should().ContainSingle();
    }

    [Fact]
    public async Task CloseForInactivityAsync_ShouldReleaseAndPublishNothing_WhenTheStoreReportsLost()
    {
        RowIs(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        _store.TryCloseInactiveAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromResult<InactivityCloseOutcome?>(null));
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;

        var outcome = await CreateSut().CloseForInactivityAsync(_tenantId, ReadRow(), ChannelType.WebChat, CancellationToken.None);

        outcome.Should().BeNull();
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseForInactivityAsync_ShouldBeANoOp_WhenATransferCommittedBetweenReadAndClose()
    {
        // The pass read agent-001's idle Active chat; a transfer to agent-002 commits before the close (state still
        // Active). The close must be decided on the owner READ, never on a fresh read of the row, so it matches nothing.
        RowIs(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        var read = ReadRow();
        var newOwner = EntityId.From("agent-002");
        _rowOwner = ConversationOwner.ForAgent(newOwner);
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;

        var outcome = await CreateSut().CloseForInactivityAsync(_tenantId, read, ChannelType.WebChat, CancellationToken.None);

        outcome.Should().BeNull("the owner changed since the read");
        _rowState.Should().Be(ConversationState.Active);
        _rowOwner!.OwnerId.Should().Be(newOwner);
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
        events.Should().BeEmpty();
        await _store.Received(1).TryCloseInactiveAsync(
            _tenantId,
            Arg.Is<InactivityCloseRequest>(r => r.ReadOwner != null && r.ReadOwner.OwnerId == _agentId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseForInactivityAsync_ShouldLogAndKeepTheClose_WhenTheReleaseFails()
    {
        RowIs(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        _capacity.ReleaseAsync(default!, default!, default, default)
                 .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("redis down")));
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;

        var outcome = await CreateSut().CloseForInactivityAsync(_tenantId, ReadRow(), ChannelType.WebChat, CancellationToken.None);

        outcome.Should().NotBeNull("a failed release does not undo the committed close (D5)");
        _rowState.Should().Be(ConversationState.Closed);
        events.Should().ContainSingle(e => e.NewState == nameof(ConversationState.Closed));
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
               .Which.Message.Should().Contain(_conversationId.Value).And.Contain(_agentId.Value).And.Contain("redis down");
    }

    [Fact]
    public async Task CloseForInactivityAsync_ShouldReleaseWithoutCancellation_WhenTheCloseWon()
    {
        RowIs(ConversationState.OnHold, ConversationOwner.ForAgent(_agentId));
        using var sweep = new CancellationTokenSource();

        var outcome = await CreateSut().CloseForInactivityAsync(_tenantId, ReadRow(), ChannelType.WebChat, sweep.Token);

        outcome.Should().NotBeNull();
        await _capacity.Received(1).ReleaseAsync(
            _tenantId, _agentId, ChannelType.WebChat, Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Theory]
    [InlineData(ConversationState.Snoozed)]
    [InlineData(ConversationState.WrapUp)]
    [InlineData(ConversationState.Queued)]
    [InlineData(ConversationState.Offered)]
    [InlineData(ConversationState.Escalated)]
    [InlineData(ConversationState.Closed)]
    public async Task CloseForInactivityAsync_ShouldNotCallTheStore_WhenTheReadStateIsOutOfScope(ConversationState state)
    {
        RowIs(state, ConversationOwner.ForAgent(_agentId));
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;

        var outcome = await CreateSut().CloseForInactivityAsync(_tenantId, ReadRow(), ChannelType.WebChat, CancellationToken.None);

        outcome.Should().BeNull();
        await _store.DidNotReceiveWithAnyArgs().TryCloseInactiveAsync(default!, default!, default);
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseForInactivityAsync_ShouldNotCallTheStore_WhenTheChannelIsVoice()
    {
        RowIs(ConversationState.Active, ConversationOwner.ForAgent(_agentId));

        var outcome = await CreateSut().CloseForInactivityAsync(_tenantId, ReadRow(), ChannelType.Voice, CancellationToken.None);

        outcome.Should().BeNull("the voice bridge owns a call's lifecycle (D3)");
        await _store.DidNotReceiveWithAnyArgs().TryCloseInactiveAsync(default!, default!, default);
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
    }

    private sealed class RecordingLogger : ILogger<ConversationSwitchboard>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
