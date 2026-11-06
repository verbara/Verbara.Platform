using System.Diagnostics.Metrics;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// conversations-return-to-last-agent task 2.3 (design D3, audit F1): the preference is consumed by the offer-state
/// write itself. The one <c>SaveAsync</c> that makes the conversation <c>Offered</c> already carries no
/// <c>_preferredAgentId</c> and the right <c>_preferredAgentOutcome</c>; the distribution-stage outcome is counted after
/// that save and before <c>conversation.offered</c>; a refused offer or a failed save consumes and counts nothing.
/// </summary>
public sealed class ConversationSwitchboardPreferenceTests : IDisposable
{
    private readonly IConversationStore _store = Substitute.For<IConversationStore>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly FakeAgentAccountStatusLookup _accountStatus = new();
    private readonly RoutingMetrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly List<string> _sequence = [];
    private readonly List<IReadOnlyDictionary<string, string>> _saved = [];
    private readonly IDisposable _subscription;

    private readonly TenantId _tenantId = new("t-pref");
    private readonly EntityId _conversationId = EntityId.From("conv-pref");
    private readonly EntityId _agentA = EntityId.From("agent-a");
    private readonly EntityId _agentB = EntityId.From("agent-b");
    private readonly EntityId _queueId = EntityId.From("queue-pref");
    private readonly DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    public ConversationSwitchboardPreferenceTests()
    {
        _clock.UtcNow.Returns(_now);
        foreach (var agent in new[] { _agentA, _agentB })
        {
            _agents.GetByIdAsync(_tenantId, agent, Arg.Any<CancellationToken>()).Returns(new Agent
            {
                AgentId = agent,
                TenantId = _tenantId,
                UserId = EntityId.From($"user-of-{agent.Value}"),
                DisplayName = agent.Value,
                State = AgentState.Available,
                CreatedAt = _now,
            });
        }

        // What the one save writes, captured at the moment SaveAsync is called (before any publish).
        _store.SaveAsync(Arg.Do<Conversation>(c =>
        {
            lock (_sequence)
            {
                _sequence.Add("save");
                _saved.Add(new Dictionary<string, string>(c.Metadata));
            }
        }), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _subscription = _eventBus.Events.Subscribe(e =>
        {
            if (e is ConversationOfferedEvent)
                lock (_sequence) _sequence.Add("offered");
        });
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, _metrics.LastAgent.Meter))
                l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (_sequence) _sequence.Add("counter:" + (string)tags[0].Value!);
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _listener.Dispose();
        _metrics.Dispose();
        _eventBus.Dispose();
    }

    private ConversationSwitchboard CreateSut() =>
        new(_store, Substitute.For<IAgentCapacityService>(), _agents, _clock, _eventBus, _accountStatus, routingMetrics: _metrics);

    private Conversation Queued(string? preferred)
    {
        var conversation = new Conversation
        {
            ConversationId = _conversationId,
            TenantId = _tenantId,
            ContactId = EntityId.From("contact-pref"),
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued,
            Owner = ConversationOwner.ForQueue(_queueId),
            CreatedAt = _now,
        };
        if (preferred is not null)
            conversation.SetMetadata(ConversationOffer.PreferredAgentIdKey, preferred);
        _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        return conversation;
    }

    [Theory]
    [InlineData("agent-a", "offered-preferred")] // the selector honoured the preference
    [InlineData("agent-b", "fallback")]          // the preferred agent was not reachable: round robin chose A
    public async Task OfferToAgentAsync_ShouldConsumeThePreferenceInTheOfferSave_WhenTheRowCarriesOne(
        string preferred, string expectedOutcome)
    {
        Queued(preferred);

        var result = await CreateSut().OfferToAgentAsync(_conversationId, _tenantId, _agentA, CancellationToken.None);

        result.Success.Should().BeTrue();
        _saved.Should().ContainSingle("one save makes the offer and consumes the preference");
        _saved[0].Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
        _saved[0].Should().Contain(ConversationOffer.PreferredAgentOutcomeKey, expectedOutcome);
        _saved[0].Should().Contain(ConversationOffer.OfferedToKey, _agentA.Value);
        _sequence.Should().Equal("save", "counter:" + expectedOutcome, "offered");
    }

    [Fact]
    public async Task OfferToAgentAsync_ShouldWriteNoOutcomeAndCountNothing_WhenTheRowCarriesNoPreference()
    {
        Queued(preferred: null);

        await CreateSut().OfferToAgentAsync(_conversationId, _tenantId, _agentA, CancellationToken.None);

        _saved.Should().ContainSingle();
        _saved[0].Should().NotContainKey(ConversationOffer.PreferredAgentOutcomeKey);
        _sequence.Should().Equal("save", "offered");
    }

    [Fact]
    public async Task OfferToAgentAsync_ShouldLeaveTheKeyAndCountNothing_WhenTheOfferIsRefused()
    {
        var conversation = Queued(_agentA.Value);
        _accountStatus.Inactive.Add(EntityId.From($"user-of-{_agentA.Value}"));

        var result = await CreateSut().OfferToAgentAsync(_conversationId, _tenantId, _agentA, CancellationToken.None);

        result.Success.Should().BeFalse();
        conversation.Metadata.Should().Contain(ConversationOffer.PreferredAgentIdKey, _agentA.Value);
        conversation.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentOutcomeKey);
        _sequence.Should().BeEmpty();
    }

    [Fact]
    public async Task OfferToAgentAsync_ShouldCountAndPublishNothing_WhenTheOfferSaveFails()
    {
        Queued(_agentA.Value);
        _store.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new TimeoutException("db")));

        var act = () => CreateSut().OfferToAgentAsync(_conversationId, _tenantId, _agentA, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        _sequence.Should().NotContain(s => s.StartsWith("counter:", StringComparison.Ordinal) || s == "offered");
    }
}
