using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Sse;
using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Routing.Inbound.Middlewares;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.Routing;

/// <summary>
/// conversations-return-to-last-agent task 2.2 (design D3, audit 2026-11-06 F2 and missed P2): both routing paths write
/// the route's preference through <see cref="RoutePreferenceStamp.Apply"/> before <c>AssignToQueueAsync</c>, persist it
/// even when the route carries no other metadata (no reason hint is configured here, so the route metadata is null),
/// and a retry with no preference removes the key an earlier attempt left. The store is detached (a copy per read and
/// per write, as Postgres builds a new object per read), so a key set on the in-memory row without a save is lost when
/// <c>AssignToQueueAsync</c> reloads it. The chain is the real <see cref="LastAgentMiddleware"/> over a fixed queue.
/// </summary>
public sealed class RoutePreferencePathTests : IDisposable
{
    private static readonly TenantId Tenant = new("t-route-pref");
    private static readonly EntityId ContactId = EntityId.From("contact-route-pref");
    private static readonly EntityId QueueId = EntityId.From("queue-route-pref");
    private static readonly EntityId AgentA = EntityId.From("agent-route-pref-a");

    private readonly InMemoryConversationStore _inner = new();
    private readonly ProbeConversationStore _store;
    private readonly IConversationStore _lookup = Substitute.For<IConversationStore>();
    private readonly LastAgentRoutingOptions _lastAgent = new() { Enabled = true };
    private readonly PlatformEventBus _eventBus = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ConversationSwitchboard _realSwitchboard;
    private readonly IConversationSwitchboard _switchboard = Substitute.For<IConversationSwitchboard>();
    private readonly IAgentSelector _selector = Substitute.For<IAgentSelector>();
    private readonly RoutingMetrics _metrics = new();
    private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
    private readonly List<string> _outcomes = [];
    private readonly IInboundRouter _router;
    private bool _failNextAssign;

    public RoutePreferencePathTests()
    {
        _store = new ProbeConversationStore(_inner, detached: true);
        _clock.UtcNow.Returns(_ => DateTimeOffset.UtcNow);
        var agents = Substitute.For<IAgentStore>();
        agents.GetByIdAsync(Tenant, AgentA, Arg.Any<CancellationToken>()).Returns(new Agent
        {
            AgentId = AgentA, TenantId = Tenant, UserId = EntityId.From("user-route-pref-a"), DisplayName = "A",
            State = AgentState.Available, CreatedAt = DateTimeOffset.UtcNow,
        });
        _realSwitchboard = new ConversationSwitchboard(
            _store, Substitute.For<IAgentCapacityService>(), agents, _clock, _eventBus,
            new FakeAgentAccountStatusLookup(), routingMetrics: _metrics);
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, _metrics.LastAgent.Meter))
                l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (_outcomes) _outcomes.Add((string)tags[0].Value!);
        });
        _listener.Start();

        // The real switchboard, except that the next assignment can be made to throw after routing's metadata save.
        _switchboard.AssignToQueueAsync(default!, default!, default!, default).ReturnsForAnyArgs(call =>
        {
            if (_failNextAssign)
            {
                _failNextAssign = false;
                throw new TimeoutException("assignment timed out");
            }

            return _realSwitchboard.AssignToQueueAsync(
                call.ArgAt<EntityId>(0), call.ArgAt<TenantId>(1), call.ArgAt<EntityId>(2), call.ArgAt<CancellationToken>(3));
        });

        var inactivity = Substitute.For<IInactivityWindowResolver>();
        inactivity.ResolveAsync(default!, default).ReturnsForAnyArgs(new InactivityWindowResolution(72, null, false));
        var lastAgent = new LastAgentMiddleware(
            _lookup, Substitute.For<IQueueStore>(), inactivity, Options.Create(_lastAgent),
            Options.Create(new ConversationOptions()), _metrics, _clock, NullLogger<LastAgentMiddleware>.Instance);
        _router = new InboundRouter([lastAgent, new FixedQueueMiddleware(QueueId)]);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _eventBus.Dispose();
        _metrics.Dispose();
    }

    private void LookupFinds(EntityId? agent) =>
        _lookup.FindLastAgentOwnerAsync(Tenant, ContactId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(agent);

    private void LookupThrows() =>
        _lookup.FindLastAgentOwnerAsync(Tenant, ContactId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

    private async Task<EntityId> SeedQueuedAsync(ChannelType channel)
    {
        var id = EntityId.New();
        await _store.SaveAsync(new Conversation
        {
            ConversationId = id,
            TenantId = Tenant,
            ContactId = ContactId,
            Channel = channel,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        return id;
    }

    private async Task<Conversation> RowAsync(EntityId id) => (await _inner.GetByIdAsync(Tenant, id, CancellationToken.None))!;

    private async Task DistributeAsync(EntityId? selects = null)
    {
        var tenants = Substitute.For<ITenantStore>();
        tenants.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns([new Tenant { TenantId = Tenant.Value, Name = "Route preference", Status = TenantStatus.Active }]);
        _selector.SelectAgentAsync(default!, default!, default, default, default).ReturnsForAnyArgs(selects);
        using var worker = new QueueDistributionWorker(
            _store, tenants, _selector, _realSwitchboard, _eventBus, _clock, new ServiceHeartbeat(),
            Options.Create(new DistributionOptions()), NullLogger<QueueDistributionWorker>.Instance);
        await worker.DistributeAsync(CancellationToken.None);
    }

    private static void ShouldBeAssigned(Conversation row)
    {
        row.State.Should().Be(ConversationState.Queued);
        row.Owner.Should().Be(ConversationOwner.ForQueue(QueueId));
    }

    private static void ShouldBeLeftForARetryCarrying(Conversation row, EntityId agent)
    {
        row.State.Should().Be(ConversationState.Queued);
        row.Owner.Should().BeNull("the assignment failed after routing's metadata save");
        row.Metadata.Should().Contain(ConversationOffer.PreferredAgentIdKey, agent.Value);
    }

    // ─── Webhook path ────────────────────────────────────────────────────────────────────────────────────────────

    private readonly IInboundMessagePipeline _pipeline = Substitute.For<IInboundMessagePipeline>();

    private WebhookInboundProcessor Webhook()
    {
        var contacts = Substitute.For<IContactStore>();
        var contact = new Contact { ContactId = ContactId, TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow };
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "5215550001234"));
        contacts.GetByIdAsync(Tenant, ContactId, Arg.Any<CancellationToken>()).Returns(contact);
        var statuses = new DeliveryStatusHandler(Substitute.For<IMessageStore>(), NullLogger<DeliveryStatusHandler>.Instance);
        var announcer = new CustomerReturnAnnouncer(
            _eventBus, Substitute.For<Verbara.Platform.Audit.IAuditService>(), new ConversationCloseMetrics(),
            NullLogger<CustomerReturnAnnouncer>.Instance);
        return new WebhookInboundProcessor(
            _pipeline, _router, _switchboard, Substitute.For<IConversationService>(), _store, contacts,
            Substitute.For<IVirtualAgent>(), Substitute.For<IConversationLifecycleService>(), statuses, _eventBus, announcer,
            NullLogger<WebhookInboundProcessor>.Instance);
    }

    private async Task WhatsAppMessageAsync(EntityId conversationId, bool opensConversation)
    {
        var message = new InboundMessage(
            new ChannelAddress(ChannelType.WhatsApp, "5215550001234"),
            new MessageEnvelope([new TextBlock("hola")]),
            "wamid." + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow);
        _pipeline.ProcessAsync(message, Tenant, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(new PipelineResult(conversationId, ContactId, EntityId.New(), IsNewConversation: opensConversation));
        await Webhook().ProcessAsync(Tenant, ChannelType.WhatsApp, WebhookResult.From([message], []), CancellationToken.None);
    }

    [Fact]
    public async Task Webhook_ShouldPersistTheKeyBeforeAssignment_WhenTheRouteHasAPreferenceAndNoMetadata()
    {
        LookupFinds(AgentA);
        var id = await SeedQueuedAsync(ChannelType.WhatsApp);

        await WhatsAppMessageAsync(id, opensConversation: true);

        var row = await RowAsync(id);
        ShouldBeAssigned(row);
        row.Metadata.Should().Contain(ConversationOffer.PreferredAgentIdKey, AgentA.Value);
    }

    [Fact]
    public async Task Webhook_ShouldWriteNoKey_WhenTheRouteHasNoPreference()
    {
        LookupFinds(null);
        var id = await SeedQueuedAsync(ChannelType.WhatsApp);

        await WhatsAppMessageAsync(id, opensConversation: true);

        var row = await RowAsync(id);
        ShouldBeAssigned(row);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
    }

    [Theory]
    [InlineData(false)] // the feature is turned off between the two attempts
    [InlineData(true)]  // the lookup fails on the second attempt
    public async Task Webhook_ShouldRemoveTheFirstAttemptsKey_WhenARetryRoutesWithNoPreference(bool lookupThrows)
    {
        LookupFinds(AgentA);
        var id = await SeedQueuedAsync(ChannelType.WhatsApp);
        _failNextAssign = true;
        await WhatsAppMessageAsync(id, opensConversation: true);
        ShouldBeLeftForARetryCarrying(await RowAsync(id), AgentA);

        if (lookupThrows)
            LookupThrows();
        else
            _lastAgent.Enabled = false;
        await WhatsAppMessageAsync(id, opensConversation: false);

        var row = await RowAsync(id);
        ShouldBeAssigned(row);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
        await DistributeAsync();
        await _selector.Received(1).SelectAgentAsync(Tenant, QueueId, ChannelType.WhatsApp, null, Arg.Any<CancellationToken>());
    }

    // ─── WebChat path ────────────────────────────────────────────────────────────────────────────────────────────

    private (WebChatInboundRouter Router, string SessionId) WebChat(EntityId conversationId)
    {
        var visitorAddress = new ChannelAddress(ChannelType.WebChat, "webchat-route-pref");
        var contact = new Contact { ContactId = ContactId, TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow };
        contact.AddAddress(visitorAddress);
        // Verified (level 2), so the identity guard lets the preference through (design D7).
        contact.AddAddress(new ChannelAddress(ChannelType.WebChat, ChannelAddress.WebChatExternalKeyPrefix + "customer-1"));
        var contacts = Substitute.For<IContactStore>();
        contacts.GetByIdAsync(Tenant, ContactId, Arg.Any<CancellationToken>()).Returns(contact);

        var sessions = new WebChatSessionManager(Options.Create(new WebChatOptions()));
        var sessionId = sessions.ConnectAsync(Tenant, ContactId, conversationId, visitorAddress, _clock).GetAwaiter().GetResult();
        var announcer = new CustomerReturnAnnouncer(
            _eventBus, Substitute.For<Verbara.Platform.Audit.IAuditService>(), new ConversationCloseMetrics(),
            NullLogger<CustomerReturnAnnouncer>.Instance);
        var router = new WebChatInboundRouter(
            _router, _switchboard, _store, contacts, _eventBus, sessions, announcer, NullLogger<WebChatInboundRouter>.Instance);
        return (router, sessionId);
    }

    private static Task VisitorMessageAsync(WebChatInboundRouter router, string sessionId, EntityId conversationId, bool opens) =>
        router.RouteFirstInboundAsync(
            sessionId, Tenant, new PipelineResult(conversationId, ContactId, EntityId.New(), IsNewConversation: opens),
            new MessageEnvelope([new TextBlock("hola")]), CancellationToken.None);

    [Fact]
    public async Task WebChat_ShouldPersistTheKeyBeforeAssignment_WhenTheRouteHasAPreferenceAndNoMetadata()
    {
        LookupFinds(AgentA);
        var id = await SeedQueuedAsync(ChannelType.WebChat);
        var (router, sessionId) = WebChat(id);

        await VisitorMessageAsync(router, sessionId, id, opens: false);

        var row = await RowAsync(id);
        ShouldBeAssigned(row);
        row.Metadata.Should().Contain(ConversationOffer.PreferredAgentIdKey, AgentA.Value);
    }

    [Fact]
    public async Task WebChat_ShouldWriteNoKey_WhenTheRouteHasNoPreference()
    {
        LookupFinds(null);
        var id = await SeedQueuedAsync(ChannelType.WebChat);
        var (router, sessionId) = WebChat(id);

        await VisitorMessageAsync(router, sessionId, id, opens: false);

        var row = await RowAsync(id);
        ShouldBeAssigned(row);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
    }

    [Fact]
    public async Task WebChat_ShouldSucceedAndAssignWithNoKey_WhenTheLookupThrows()
    {
        LookupThrows();
        var id = await SeedQueuedAsync(ChannelType.WebChat);
        var (router, sessionId) = WebChat(id);

        var act = () => VisitorMessageAsync(router, sessionId, id, opens: false);

        await act.Should().NotThrowAsync();
        var row = await RowAsync(id);
        ShouldBeAssigned(row);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebChat_ShouldRemoveTheFirstAttemptsKey_WhenARetryRoutesWithNoPreference(bool lookupThrows)
    {
        LookupFinds(AgentA);
        var id = await SeedQueuedAsync(ChannelType.WebChat);
        var (router, sessionId) = WebChat(id);
        _failNextAssign = true;
        var first = () => VisitorMessageAsync(router, sessionId, id, opens: false);
        await first.Should().ThrowAsync<TimeoutException>("the WebChat path lets a non-routing failure through, as today");
        ShouldBeLeftForARetryCarrying(await RowAsync(id), AgentA);

        if (lookupThrows)
            LookupThrows();
        else
            _lastAgent.Enabled = false;
        await VisitorMessageAsync(router, sessionId, id, opens: false);

        var row = await RowAsync(id);
        ShouldBeAssigned(row);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
        await DistributeAsync();
        await _selector.Received(1).SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, null, Arg.Any<CancellationToken>());
    }

    // ─── A new WebChat session after the first offer (review round, spec "The preference MUST be used once") ─────

    [Fact]
    public async Task WebChat_ShouldNotPreferAgain_WhenANewSessionWritesAfterThePreferredAgentDeclined()
    {
        LookupFinds(AgentA);
        var id = await SeedQueuedAsync(ChannelType.WebChat);
        var (firstRouter, firstSession) = WebChat(id);
        await VisitorMessageAsync(firstRouter, firstSession, id, opens: false);
        await DistributeAsync(selects: AgentA);
        (await _realSwitchboard.RejectAsync(id, Tenant, AgentA, CancellationToken.None)).Success.Should().BeTrue();

        // The visitor reloads the page: a new session, whose first message is stored in the same conversation.
        var (secondRouter, secondSession) = WebChat(id);
        await VisitorMessageAsync(secondRouter, secondSession, id, opens: false);

        var row = await RowAsync(id);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey, "the first offer consumed the preference");
        row.Metadata.Should().Contain(ConversationOffer.PreferredAgentOutcomeKey, LastAgentOutcomes.OfferedPreferred);
        await DistributeAsync(selects: AgentA);
        await _selector.Received(1).SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, null, Arg.Any<CancellationToken>());
        _outcomes.Should().Equal(LastAgentOutcomes.Stamped, LastAgentOutcomes.OfferedPreferred);
    }

    [Fact]
    public async Task WebChat_ShouldKeepTheOpenOffer_WhenANewSessionWritesDuringIt()
    {
        LookupFinds(AgentA);
        var id = await SeedQueuedAsync(ChannelType.WebChat);
        var (firstRouter, firstSession) = WebChat(id);
        await VisitorMessageAsync(firstRouter, firstSession, id, opens: false);
        await DistributeAsync(selects: AgentA);
        var offered = await RowAsync(id);
        offered.State.Should().Be(ConversationState.Offered);

        var (secondRouter, secondSession) = WebChat(id);
        await VisitorMessageAsync(secondRouter, secondSession, id, opens: false);

        var row = await RowAsync(id);
        row.State.Should().Be(ConversationState.Offered, "a new session does not pull an open offer back to the queue");
        row.Owner.Should().Be(ConversationOwner.ForQueue(QueueId));
        row.Metadata.Should().Contain(ConversationOffer.OfferedToKey, AgentA.Value);
        row.Metadata.Should().Contain(ConversationOffer.OfferedAtKey, offered.Metadata[ConversationOffer.OfferedAtKey]);
        row.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
        _outcomes.Should().Equal(LastAgentOutcomes.Stamped, LastAgentOutcomes.OfferedPreferred);
    }

    [Fact]
    public async Task WebChat_ShouldNotRouteAgain_WhenANewSessionWritesToAConversationAlreadyInItsQueue()
    {
        LookupFinds(AgentA);
        var id = await SeedQueuedAsync(ChannelType.WebChat);
        var (firstRouter, firstSession) = WebChat(id);
        await VisitorMessageAsync(firstRouter, firstSession, id, opens: false);
        var queued = await RowAsync(id);

        var (secondRouter, secondSession) = WebChat(id);
        await VisitorMessageAsync(secondRouter, secondSession, id, opens: false);

        var row = await RowAsync(id);
        ShouldBeAssigned(row);
        row.Metadata.Should().Equal(queued.Metadata, "the queue clock and the pending preference stay as the first routing left them");
        _outcomes.Should().Equal(LastAgentOutcomes.Stamped);
    }

    // ─── Counted per routing attempt (design D8, task 2.1) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Webhook_ShouldCountTwoStampedAndOneOfferedPreferred_WhenTheSameConversationIsRoutedTwiceAndThenOffered()
    {
        // An assertion of the documented per-attempt semantics, not a de-duplication.
        LookupFinds(AgentA);
        var id = await SeedQueuedAsync(ChannelType.WhatsApp);
        _failNextAssign = true;
        await WhatsAppMessageAsync(id, opensConversation: true);
        await WhatsAppMessageAsync(id, opensConversation: false);
        ShouldBeAssigned(await RowAsync(id));

        await DistributeAsync(selects: AgentA);

        (await RowAsync(id)).State.Should().Be(ConversationState.Offered);
        _outcomes.Should().Equal(LastAgentOutcomes.Stamped, LastAgentOutcomes.Stamped, LastAgentOutcomes.OfferedPreferred);
    }

    // ─── Apply itself ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_ShouldReportWhetherItChangedTheRow_WhenItSetsKeepsOrRemovesTheKey()
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(), TenantId = Tenant, ContactId = ContactId, Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued, CreatedAt = DateTimeOffset.UtcNow,
        };
        var preferred = new RouteResult(QueueId, MessagePriority.Normal, AgentA, null);
        var none = new RouteResult(QueueId, MessagePriority.Normal, null, null);

        RoutePreferenceStamp.Apply(conversation, none).Should().BeFalse();
        RoutePreferenceStamp.Apply(conversation, preferred).Should().BeTrue();
        conversation.Metadata.Should().Contain(ConversationOffer.PreferredAgentIdKey, AgentA.Value);
        RoutePreferenceStamp.Apply(conversation, preferred).Should().BeFalse();
        RoutePreferenceStamp.Apply(conversation, none).Should().BeTrue();
        conversation.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
    }

    [Fact]
    public void Apply_ShouldWriteNoPreference_WhenThePreferenceWasAlreadyConsumed()
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(), TenantId = Tenant, ContactId = ContactId, Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued, CreatedAt = DateTimeOffset.UtcNow,
        };
        conversation.SetMetadata(ConversationOffer.PreferredAgentOutcomeKey, LastAgentOutcomes.Fallback);

        RoutePreferenceStamp.Apply(conversation, new RouteResult(QueueId, MessagePriority.Normal, AgentA, null))
            .Should().BeFalse();

        conversation.Metadata.Should().NotContainKey(ConversationOffer.PreferredAgentIdKey);
        conversation.Metadata.Should().Contain(ConversationOffer.PreferredAgentOutcomeKey, LastAgentOutcomes.Fallback);
    }

    private sealed class FixedQueueMiddleware(EntityId queueId) : InboundRoutingMiddlewareBase
    {
        public override Task<RouteResult?> RouteAsync(
            RoutingContext context, Func<Task<RouteResult?>> continuation, CancellationToken ct) =>
            Task.FromResult<RouteResult?>(new RouteResult(queueId, MessagePriority.Normal, null, null));
    }
}
