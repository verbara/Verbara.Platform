using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Sse;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.Routing;

/// <summary>
/// conversations-return-to-last-agent task 2.3 (design D3, audit F1): the worker reads <c>_preferredAgentId</c> from the
/// queued row and passes it to the selector; the switchboard's offer save consumes it. Over the real switchboard and a
/// detached in-memory store (a copy per read and per write, as Postgres).
/// </summary>
public sealed class PreferenceDistributionTests : IDisposable
{
    private static readonly TenantId Tenant = new("t-pref-dist");
    private static readonly EntityId QueueId = EntityId.From("queue-pref-dist");
    private static readonly EntityId AgentA = EntityId.From("agent-pref-a");
    private static readonly EntityId AgentB = EntityId.From("agent-pref-b");

    private readonly InMemoryConversationStore _inner = new();
    private readonly ProbeConversationStore _store;
    private readonly PlatformEventBus _eventBus = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAgentSelector _selector = Substitute.For<IAgentSelector>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly ConversationSwitchboard _switchboard;
    private readonly List<ConversationOfferedEvent> _offered = [];
    private readonly IDisposable _subscription;
    private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    public PreferenceDistributionTests()
    {
        _store = new ProbeConversationStore(_inner, detached: true);
        _clock.UtcNow.Returns(_ => _now);
        _tenants.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns([new Tenant { TenantId = Tenant.Value, Name = "Preference distribution", Status = TenantStatus.Active }]);
        var agents = Substitute.For<IAgentStore>();
        foreach (var agent in new[] { AgentA, AgentB })
        {
            agents.GetByIdAsync(Tenant, agent, Arg.Any<CancellationToken>()).Returns(new Agent
            {
                AgentId = agent, TenantId = Tenant, UserId = EntityId.From("user-" + agent.Value),
                DisplayName = agent.Value, State = AgentState.Available, CreatedAt = _now,
            });
        }

        _switchboard = new ConversationSwitchboard(
            _store, Substitute.For<IAgentCapacityService>(), agents, _clock, _eventBus, new FakeAgentAccountStatusLookup());
        _subscription = _eventBus.Events.Subscribe(e =>
        {
            if (e is ConversationOfferedEvent offered)
                lock (_offered) _offered.Add(offered);
        });
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _eventBus.Dispose();
    }

    private async Task<EntityId> SeedQueuedAsync(string? preferred)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued,
            Owner = ConversationOwner.ForQueue(QueueId),
            CreatedAt = _now,
        };
        if (preferred is not null)
            conversation.SetMetadata(ConversationOffer.PreferredAgentIdKey, preferred);
        await _store.SaveAsync(conversation, CancellationToken.None);
        return conversation.ConversationId;
    }

    private void SelectorChooses(EntityId? whenPreferredA, EntityId? otherwise)
    {
        _selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WhatsApp, Arg.Any<EntityId?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<EntityId?>(3) == AgentA ? whenPreferredA : otherwise);
    }

    private QueueDistributionWorker Worker() => new(
        _store, _tenants, _selector, _switchboard, _eventBus, _clock, new ServiceHeartbeat(),
        Options.Create(new DistributionOptions()), NullLogger<QueueDistributionWorker>.Instance);

    private async Task DistributeAsync()
    {
        using var worker = Worker();
        await worker.DistributeAsync(CancellationToken.None);
    }

    private async Task<Conversation> RowAsync(EntityId id) => (await _inner.GetByIdAsync(Tenant, id, CancellationToken.None))!;

    // ─── Worker ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DistributeAsync_ShouldPassThePreferenceAndConsumeIt_WhenThePreferredAgentIsSelected()
    {
        var id = await SeedQueuedAsync(AgentA.Value);
        SelectorChooses(whenPreferredA: AgentA, otherwise: AgentB);

        await DistributeAsync();

        await _selector.Received(1).SelectAgentAsync(Tenant, QueueId, ChannelType.WhatsApp, AgentA, Arg.Any<CancellationToken>());
        var row = await RowAsync(id);
        row.State.Should().Be(ConversationState.Offered);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
        row.Metadata.Should().Contain(ConversationOffer.PreferredAgentOutcomeKey, ConversationOffer.PreferredOutcomeOfferedPreferred);
        _offered.Should().ContainSingle("one offer reaches the console as one conversation.offered (task 1.0)")
            .Which.AgentId.Should().Be(AgentA.Value);
    }

    [Fact]
    public async Task DistributeAsync_ShouldRecordAFallback_WhenTheSelectorChoosesAnotherAgent()
    {
        var id = await SeedQueuedAsync(AgentA.Value);
        SelectorChooses(whenPreferredA: AgentB, otherwise: AgentB);

        await DistributeAsync();

        await _selector.Received(1).SelectAgentAsync(Tenant, QueueId, ChannelType.WhatsApp, AgentA, Arg.Any<CancellationToken>());
        var row = await RowAsync(id);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
        row.Metadata.Should().Contain(ConversationOffer.PreferredAgentOutcomeKey, ConversationOffer.PreferredOutcomeFallback);
        _offered.Should().ContainSingle().Which.AgentId.Should().Be(AgentB.Value);
    }

    [Fact]
    public async Task DistributeAsync_ShouldKeepTheKey_WhenNobodyIsSelected()
    {
        var id = await SeedQueuedAsync(AgentA.Value);
        SelectorChooses(whenPreferredA: null, otherwise: null);

        await DistributeAsync();

        var row = await RowAsync(id);
        row.State.Should().Be(ConversationState.Queued);
        row.Metadata.Should().Contain(ConversationOffer.PreferredAgentIdKey, AgentA.Value);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentOutcomeKey);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public async Task DistributeAsync_ShouldPassNoPreference_WhenTheKeyIsMalformed(string malformed)
    {
        await SeedQueuedAsync(malformed);
        SelectorChooses(whenPreferredA: null, otherwise: null);

        await DistributeAsync();

        await _selector.Received(1).SelectAgentAsync(Tenant, QueueId, ChannelType.WhatsApp, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Host_ShouldGiveTheSwitchboardTheRoutingCounter_WhenItBuildsTheSwitchboard()
    {
        // The distribution-stage outcome is counted by the switchboard; the host must inject the counter it exports.
        using var factory = new PlatformApiFactory();

        var switchboard = factory.Services.GetService(typeof(IConversationSwitchboard));
        var metrics = factory.Services.GetService(typeof(RoutingMetrics));

        var field = typeof(ConversationSwitchboard).GetField("_routingMetrics",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        switchboard.Should().BeOfType<ConversationSwitchboard>();
        field!.GetValue(switchboard).Should().BeSameAs(metrics).And.NotBeNull();
    }

    // ─── Fault injection: the offer committed, the worker's next write never ran ─────────────────────────────────

    /// <summary>
    /// From now on, the first read of <paramref name="id"/> that finds it <c>Offered</c> (the worker's re-load after the
    /// switchboard's offer save committed) throws, once, as a crash or a DB error between the two writes would.
    /// </summary>
    private void FailTheWorkersReloadAfterTheOfferCommits(EntityId id)
    {
        var fired = 0;
        _store.OnRead(id, (row, _) =>
        {
            if (row?.State == ConversationState.Offered && Interlocked.Exchange(ref fired, 1) == 0)
                throw new TimeoutException("connection lost after the offer committed");
        });
    }

    [Theory]
    [InlineData(true)]  // the offer expires through the timeout worker
    [InlineData(false)] // A rejects it
    public async Task DistributeAsync_ShouldNotPreferAgain_WhenTheCycleIsInterruptedAfterTheOfferCommits(bool expire)
    {
        var id = await SeedQueuedAsync(AgentA.Value);
        SelectorChooses(whenPreferredA: AgentA, otherwise: AgentB);
        FailTheWorkersReloadAfterTheOfferCommits(id);

        Func<Task> interrupted = DistributeAsync;
        await interrupted.Should().ThrowAsync<TimeoutException>();
        (await RowAsync(id)).State.Should().Be(ConversationState.Offered, "the offer save committed before the fault");

        if (expire)
        {
            _now = _now.AddSeconds(new DistributionOptions().OfferTimeoutSeconds + 1);
            using var timeouts = new ConversationTimeoutWorker(
                _store, _tenants, _switchboard, _eventBus, _clock, new ServiceHeartbeat(),
                Options.Create(new DistributionOptions()), NullLogger<ConversationTimeoutWorker>.Instance,
                conversationOptions: Options.Create(new ConversationOptions { InactivityCloseEnabled = false }));
            await timeouts.ProcessTimeoutsAsync(CancellationToken.None);
        }
        else
        {
            (await _switchboard.RejectAsync(id, Tenant, AgentA, CancellationToken.None)).Success.Should().BeTrue();
        }

        var requeued = await RowAsync(id);
        requeued.State.Should().Be(ConversationState.Queued);
        requeued.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);

        _selector.ClearReceivedCalls();
        await DistributeAsync();

        await _selector.Received(1).SelectAgentAsync(Tenant, QueueId, ChannelType.WhatsApp, null, Arg.Any<CancellationToken>());
        _offered.Last().AgentId.Should().Be(AgentB.Value, "round robin, not the preference, chose this offer");
    }
}
