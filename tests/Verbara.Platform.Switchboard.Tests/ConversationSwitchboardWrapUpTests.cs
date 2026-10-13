using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// <see cref="ConversationSwitchboard.WrapUpAsync"/> is the one way an agent-held digital conversation
/// enters <c>WrapUp</c> and the one point its owner's capacity unit is released (design D2/D3): the
/// store's state-and-owner update decides the winner, and only the winner releases, mirrors the state
/// and publishes <see cref="ConversationStateChangedEvent"/>.
/// </summary>
public sealed class ConversationSwitchboardWrapUpTests : IDisposable
{
    private readonly IConversationStore _store = Substitute.For<IConversationStore>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly FakeAgentAccountStatusLookup _accountStatus = new();

    private readonly TenantId _tenantId = new("t1");
    private readonly EntityId _conversationId = EntityId.From("conv-001");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private readonly DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    public ConversationSwitchboardWrapUpTests() => _clock.UtcNow.Returns(_now);

    public void Dispose() => _eventBus.Dispose();

    private ConversationSwitchboard CreateSut() =>
        new(_store, _capacity, _agents, _clock, _eventBus, _accountStatus);

    private Conversation BuildChat(ConversationState state, ConversationOwner? owner) =>
        new()
        {
            ConversationId = _conversationId,
            TenantId = _tenantId,
            ContactId = EntityId.From("contact-001"),
            Channel = ChannelType.WebChat,
            State = state,
            Owner = owner,
            CreatedAt = _now.AddMinutes(-10),
        };

    // Every load hands back a fresh copy, as the Postgres store does.
    private void StoreHolds(ConversationState state, ConversationOwner? owner) =>
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
              .Returns(_ => BuildChat(state, owner));

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

    [Fact]
    public async Task WrapUpAsync_ShouldReleaseOnce_WhenTwoCallersRace()
    {
        StoreHolds(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        // The store's conditional update lets exactly one caller through.
        _store.TryCommitWrapUpAsync(_tenantId, _conversationId, _agentId, _now, Arg.Any<CancellationToken>())
              .Returns(true, false);
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;
        var sut = CreateSut();

        var results = await Task.WhenAll(
            sut.WrapUpAsync(_tenantId, _conversationId, _agentId, CancellationToken.None),
            sut.WrapUpAsync(_tenantId, _conversationId, _agentId, CancellationToken.None));

        results.Count(r => r.Success).Should().Be(1, "only the caller whose update won wraps up");
        await _capacity.Received(1).ReleaseAsync(_tenantId, _agentId, ChannelType.WebChat, Arg.Any<CancellationToken>());
        events.Should().ContainSingle(e => e.NewState == nameof(ConversationState.WrapUp));
    }

    [Fact]
    public async Task WrapUpAsync_ShouldNotRelease_WhenOwnerChangedConcurrently()
    {
        // The caller read agent-001 as the owner; a transfer swapped the owner (state still Active)
        // before the update ran, so the state-and-owner predicate matches nothing.
        StoreHolds(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        _store.TryCommitWrapUpAsync(_tenantId, _conversationId, _agentId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
              .Returns(false);
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;

        var result = await CreateSut().WrapUpAsync(_tenantId, _conversationId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse();
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task WrapUpAsync_ShouldFailWithoutCommitOrRelease_WhenOwnerDiffersFromExpected()
    {
        // The caller authorized against agent-001; a transfer to agent-002 committed before the wrap-up
        // read the row. The wrap-up must not act on agent-002's chat (design D3).
        var newOwner = EntityId.From("agent-002");
        StoreHolds(ConversationState.Active, ConversationOwner.ForAgent(newOwner));
        _store.TryCommitWrapUpAsync(default!, default!, default!, default, default).ReturnsForAnyArgs(true);
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;

        var result = await CreateSut().WrapUpAsync(_tenantId, _conversationId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse("the owner the caller was authorized against no longer holds the chat");
        await _store.DidNotReceiveWithAnyArgs().TryCommitWrapUpAsync(default!, default!, default!, default, default);
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task WrapUpAsync_ShouldReleaseWithoutCancellation_WhenWrapUpCommitted()
    {
        // Once the wrap-up is committed the release must not be abandoned because the caller (an HTTP
        // request) went away: it runs with CancellationToken.None, as the voice bridge's release does.
        StoreHolds(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        _store.TryCommitWrapUpAsync(_tenantId, _conversationId, _agentId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
              .Returns(true);
        using var request = new CancellationTokenSource();

        var result = await CreateSut().WrapUpAsync(_tenantId, _conversationId, _agentId, request.Token);

        result.Success.Should().BeTrue();
        await _capacity.Received(1).ReleaseAsync(
            _tenantId, _agentId, ChannelType.WebChat, Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Theory]
    [InlineData(ConversationState.Queued)]
    [InlineData(ConversationState.Offered)]
    [InlineData(ConversationState.WrapUp)]
    [InlineData(ConversationState.Closed)]
    public async Task WrapUpAsync_ShouldFailWithoutRelease_WhenStateIsNotAgentHeld(ConversationState state)
    {
        StoreHolds(state, ConversationOwner.ForAgent(_agentId));

        var result = await CreateSut().WrapUpAsync(_tenantId, _conversationId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().TryCommitWrapUpAsync(default!, default!, default!, default, default);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task WrapUpAsync_ShouldNotRelease_WhenOwnerIsNotAnAgent()
    {
        StoreHolds(ConversationState.Active, ConversationOwner.ForBot(EntityId.From("bot-001")));

        var result = await CreateSut().WrapUpAsync(_tenantId, _conversationId, _agentId, CancellationToken.None);

        result.Success.Should().BeFalse("only an agent-held conversation wraps up through this path");
        await _store.DidNotReceiveWithAnyArgs().TryCommitWrapUpAsync(default!, default!, default!, default, default);
        await _capacity.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task WrapUpAsync_ShouldStillCommit_WhenCapacityReleaseThrows()
    {
        StoreHolds(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
        _store.TryCommitWrapUpAsync(_tenantId, _conversationId, _agentId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
              .Returns(true);
        _capacity.ReleaseAsync(_tenantId, _agentId, ChannelType.WebChat, Arg.Any<CancellationToken>())
                 .Returns<Task>(_ => throw new InvalidOperationException("capacity ledger unavailable"));
        var events = RecordStateChanges(out var subscription);
        using var _ = subscription;

        var result = await CreateSut().WrapUpAsync(_tenantId, _conversationId, _agentId, CancellationToken.None);

        result.Success.Should().BeTrue("the wrap-up is committed; a failed release is logged, not surfaced");
        result.NewState.Should().Be(ConversationState.WrapUp);
        events.Should().ContainSingle(e => e.OldState == nameof(ConversationState.Active)
                                           && e.NewState == nameof(ConversationState.WrapUp));
    }
}
