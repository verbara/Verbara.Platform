using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound.Middlewares;
using Verbara.Platform.Typification.Resolution;

namespace Verbara.Platform.Routing.Inbound.Tests;

/// <summary>
/// conversations-return-to-last-agent task 2.1 (design D2, D4, D6, D7, D8, D10): the continuation-first rewrite, its
/// outcomes, the per-queue switch read for the final queue, the clamp to the tenant's inactivity window, the catch
/// around every read it adds, and its slot outside ChannelQueueMapping in the real chain.
/// </summary>
public sealed class LastAgentMiddlewareTests : IDisposable
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId ContactId = EntityId.From("contact-1");
    private static readonly EntityId AgentA = EntityId.From("agent-a");
    private static readonly EntityId Q1 = EntityId.From("q-1");
    private static readonly EntityId Q2 = EntityId.From("q-2");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IQueueStore _queues = Substitute.For<IQueueStore>();
    private readonly IInactivityWindowResolver _inactivity = Substitute.For<IInactivityWindowResolver>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly RoutingMetrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly List<string> _outcomes = [];

    public LastAgentMiddlewareTests()
    {
        _clock.UtcNow.Returns(Now);
        _inactivity.ResolveAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new InactivityWindowResolution(72, null, OverrideApplied: false));
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, _metrics.LastAgent.Meter))
                l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (_outcomes)
                _outcomes.Add((string)tags[0].Value!);
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    private static Contact MakeContact(params ChannelAddress[] addresses)
    {
        var contact = new Contact { ContactId = ContactId, TenantId = Tenant, CreatedAt = Now };
        foreach (var address in addresses)
            contact.AddAddress(address);
        return contact;
    }

    private static RoutingContext MakeContext(ChannelType channel = ChannelType.WhatsApp, Contact? contact = null) => new(
        Conversation: new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = ContactId,
            Channel = channel,
            State = ConversationState.Queued,
            CreatedAt = Now,
        },
        Contact: contact ?? MakeContact(new ChannelAddress(ChannelType.WhatsApp, "5215550000001")),
        Channel: channel,
        InitialMessage: null,
        TenantId: Tenant);

    private static Queue MakeQueue(EntityId id, bool? lastAgent, HoursOfOperation? hours = null, EntityId? overflowTo = null) => new()
    {
        QueueId = id,
        TenantId = Tenant,
        Name = id.Value,
        LastAgentRoutingEnabled = lastAgent,
        Hours = hours,
        OverflowRule = overflowTo is { } o ? new QueueOverflowRule { OverflowQueueId = o, OverflowAfterSeconds = 300 } : null,
        CreatedAt = Now,
    };

    private LastAgentMiddleware Middleware(
        bool hostEnabled = true, int windowHours = 72, bool inactivityCloseEnabled = true) => new(
        _conversations,
        _queues,
        _inactivity,
        Options.Create(new LastAgentRoutingOptions { Enabled = hostEnabled, WindowHours = windowHours }),
        Options.Create(new ConversationOptions { InactivityCloseEnabled = inactivityCloseEnabled }),
        _metrics,
        _clock,
        NullLogger<LastAgentMiddleware>.Instance);

    private static Func<Task<RouteResult?>> RouteTo(EntityId queueId) =>
        () => Task.FromResult<RouteResult?>(new RouteResult(queueId, MessagePriority.Normal, null, null));

    private void OwnerIs(EntityId? agent) =>
        _conversations.FindLastAgentOwnerAsync(Tenant, ContactId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(agent);

    // ─── Outcomes ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteAsync_ShouldStampTheLastOwner_WhenTheFeatureIsOnAndAnOwnerIsFound()
    {
        OwnerIs(AgentA);

        var result = await Middleware().RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None);

        result!.QueueId.Should().Be(Q1);
        result.PreferredAgentId.Should().Be(AgentA);
        _outcomes.Should().Equal(LastAgentOutcomes.Stamped);
    }

    [Fact]
    public async Task RouteAsync_ShouldCountNoHistory_WhenNoOwnerIsFound()
    {
        OwnerIs(null);

        var result = await Middleware().RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None);

        result!.PreferredAgentId.Should().BeNull();
        _outcomes.Should().Equal(LastAgentOutcomes.NoHistory);
    }

    [Fact]
    public async Task RouteAsync_ShouldCountDisabledAndSkipTheLookup_WhenTheHostIsOffAndTheQueueFollowsIt()
    {
        _queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>()).Returns(MakeQueue(Q1, lastAgent: null));
        OwnerIs(AgentA);

        var result = await Middleware(hostEnabled: false).RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None);

        result!.PreferredAgentId.Should().BeNull();
        _outcomes.Should().Equal(LastAgentOutcomes.Disabled);
        await _conversations.DidNotReceiveWithAnyArgs().FindLastAgentOwnerAsync(default, default, default, default);
    }

    [Fact]
    public async Task RouteAsync_ShouldFollowTheHost_WhenTheQueueIsMissingFromTheStore()
    {
        _queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>()).Returns((Queue?)null);
        OwnerIs(AgentA);

        (await Middleware(hostEnabled: false).RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None))!
            .PreferredAgentId.Should().BeNull();
        (await Middleware(hostEnabled: true).RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None))!
            .PreferredAgentId.Should().Be(AgentA);
        _outcomes.Should().Equal(LastAgentOutcomes.Disabled, LastAgentOutcomes.Stamped);
    }

    [Fact]
    public async Task RouteAsync_ShouldStamp_WhenTheQueueOverrideIsOnAndTheHostIsOff()
    {
        _queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>()).Returns(MakeQueue(Q1, lastAgent: true));
        OwnerIs(AgentA);

        var result = await Middleware(hostEnabled: false).RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None);

        result!.PreferredAgentId.Should().Be(AgentA);
        _outcomes.Should().Equal(LastAgentOutcomes.Stamped);
    }

    [Fact]
    public async Task RouteAsync_ShouldCountDisabled_WhenTheQueueOverrideIsOffAndTheHostIsOn()
    {
        _queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>()).Returns(MakeQueue(Q1, lastAgent: false));
        OwnerIs(AgentA);

        var result = await Middleware(hostEnabled: true).RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None);

        result!.PreferredAgentId.Should().BeNull();
        _outcomes.Should().Equal(LastAgentOutcomes.Disabled);
    }

    [Fact]
    public async Task RouteAsync_ShouldReadTheQueueTheContinuationReturned_WhenItDiffersFromTheContextConversation()
    {
        // The route's final queue (Q2) decides, read after the continuation; Q1 would say off.
        _queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>()).Returns(MakeQueue(Q1, lastAgent: false));
        _queues.GetByIdAsync(Tenant, Q2, Arg.Any<CancellationToken>()).Returns(MakeQueue(Q2, lastAgent: true));
        OwnerIs(AgentA);

        var result = await Middleware(hostEnabled: false).RouteAsync(MakeContext(), RouteTo(Q2), CancellationToken.None);

        result!.PreferredAgentId.Should().Be(AgentA);
    }

    // ─── Identity guard (D7) ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteAsync_ShouldSkip_WhenTheContactIsASharedPlaceholder()
    {
        OwnerIs(AgentA);
        var shared = MakeContact(new ChannelAddress(ChannelType.Voice, SharedContactAddresses.AnonymousVoiceCaller));

        var result = await Middleware().RouteAsync(MakeContext(ChannelType.WhatsApp, shared), RouteTo(Q1), CancellationToken.None);

        result!.PreferredAgentId.Should().BeNull();
        _outcomes.Should().Equal(LastAgentOutcomes.SkippedIdentity);
        await _conversations.DidNotReceiveWithAnyArgs().FindLastAgentOwnerAsync(default, default, default, default);
    }

    [Fact]
    public async Task RouteAsync_ShouldSkip_WhenAWebChatContactIsUnverified()
    {
        OwnerIs(AgentA);
        var level1 = MakeContact(new ChannelAddress(ChannelType.WebChat, ChannelAddress.WebChatVisitorKeyPrefix + "v-1"));
        level1.IdentityLevel.Should().Be(ContactIdentityLevel.Unverified);

        var result = await Middleware().RouteAsync(MakeContext(ChannelType.WebChat, level1), RouteTo(Q1), CancellationToken.None);

        result!.PreferredAgentId.Should().BeNull();
        _outcomes.Should().Equal(LastAgentOutcomes.SkippedIdentity);
    }

    [Fact]
    public async Task RouteAsync_ShouldStamp_WhenAWebChatContactIsVerified()
    {
        OwnerIs(AgentA);
        var verified = MakeContact(
            new ChannelAddress(ChannelType.WebChat, ChannelAddress.WebChatVisitorKeyPrefix + "v-1"),
            new ChannelAddress(ChannelType.WebChat, ChannelAddress.WebChatExternalKeyPrefix + "customer-1"));
        verified.IdentityLevel.Should().Be(ContactIdentityLevel.Verified);

        var result = await Middleware().RouteAsync(MakeContext(ChannelType.WebChat, verified), RouteTo(Q1), CancellationToken.None);

        result!.PreferredAgentId.Should().Be(AgentA);
    }

    // ─── Catch (D6) and the continuation (D8) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteAsync_ShouldReturnTheRouteWithNoPreference_WhenTheLookupThrows()
    {
        _conversations.FindLastAgentOwnerAsync(Tenant, ContactId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<EntityId?>>(_ => throw new InvalidOperationException("db down"));

        var result = await Middleware().RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None);

        result!.QueueId.Should().Be(Q1);
        result.PreferredAgentId.Should().BeNull();
        _outcomes.Should().Equal(LastAgentOutcomes.LookupFailed);
    }

    [Fact]
    public async Task RouteAsync_ShouldReturnTheRouteWithNoPreference_WhenTheQueueReadThrows()
    {
        _queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>())
            .Returns<Task<Queue?>>(_ => throw new InvalidOperationException("db down"));

        var result = await Middleware(hostEnabled: false).RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None);

        result!.QueueId.Should().Be(Q1);
        result.PreferredAgentId.Should().BeNull();
        _outcomes.Should().Equal(LastAgentOutcomes.LookupFailed);
    }

    [Fact]
    public async Task RouteAsync_ShouldCountLookupFailed_WhenTheInactivityWindowReadThrows()
    {
        OwnerIs(AgentA);
        _inactivity.ResolveAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns<Task<InactivityWindowResolution>>(_ => throw new InvalidOperationException("db down"));

        var result = await Middleware().RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None);

        result!.PreferredAgentId.Should().BeNull();
        _outcomes.Should().Equal(LastAgentOutcomes.LookupFailed);
    }

    [Fact]
    public async Task RouteAsync_ShouldPropagateCancellation_WhenTheRequestIsCancelledDuringTheLookup()
    {
        using var cts = new CancellationTokenSource();
        _conversations.FindLastAgentOwnerAsync(Tenant, ContactId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<EntityId?>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var act = () => Middleware().RouteAsync(MakeContext(), RouteTo(Q1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task RouteAsync_ShouldCountNothing_WhenTheContinuationThrows()
    {
        OwnerIs(AgentA);

        var act = () => Middleware().RouteAsync(
            MakeContext(), () => throw new InvalidOperationException("outside business hours"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _outcomes.Should().BeEmpty();
        await _conversations.DidNotReceiveWithAnyArgs().FindLastAgentOwnerAsync(default, default, default, default);
    }

    [Fact]
    public async Task RouteAsync_ShouldCountNothing_WhenTheContinuationReturnsNoRoute()
    {
        var result = await Middleware().RouteAsync(MakeContext(), () => Task.FromResult<RouteResult?>(null), CancellationToken.None);

        result.Should().BeNull();
        _outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task RouteAsync_ShouldCountOncePerAttempt_WhenTheSameConversationIsRoutedTwice()
    {
        OwnerIs(AgentA);
        var context = MakeContext();

        await Middleware().RouteAsync(context, RouteTo(Q1), CancellationToken.None);
        await Middleware().RouteAsync(context, RouteTo(Q1), CancellationToken.None);

        _outcomes.Should().Equal(LastAgentOutcomes.Stamped, LastAgentOutcomes.Stamped);
    }

    // ─── The window and the clamp (D4, ruling Q12) ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(72, true, 72, 72)]     // defaults
    [InlineData(72, true, 120, 120)]   // tenant inactivity override above the window: the clamp reaches 120 h
    [InlineData(72, false, 120, 72)]   // inactivity close off: the configured window as is
    [InlineData(200, true, 120, 200)]  // a window above the inactivity window is kept
    public async Task RouteAsync_ShouldReachBackTheEffectiveWindow_WhenItComputesSince(
        int windowHours, bool inactivityCloseEnabled, int inactivityHours, int expectedHours)
    {
        _inactivity.ResolveAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new InactivityWindowResolution(inactivityHours, inactivityHours, OverrideApplied: true));
        OwnerIs(null);

        await Middleware(windowHours: windowHours, inactivityCloseEnabled: inactivityCloseEnabled)
            .RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None);

        await _conversations.Received(1).FindLastAgentOwnerAsync(
            Tenant, ContactId, Now - TimeSpan.FromHours(expectedHours), Arg.Any<CancellationToken>());
        if (!inactivityCloseEnabled)
            await _inactivity.DidNotReceiveWithAnyArgs().ResolveAsync(default, default);
    }

    [Fact]
    public async Task RouteAsync_ShouldAdmitARow100HoursOld_OnlyWhileTheInactivityCloseIsOnWithA120HourTenantWindow()
    {
        // The store's own semantics (task 1.3's contract): a row qualifies when its last_message_at >= since.
        var lastMessageAt = Now - TimeSpan.FromHours(100);
        _conversations.FindLastAgentOwnerAsync(Tenant, ContactId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<DateTimeOffset>(2) <= lastMessageAt ? AgentA : (EntityId?)null);
        _inactivity.ResolveAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new InactivityWindowResolution(120, 120, OverrideApplied: true));

        (await Middleware(inactivityCloseEnabled: true).RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None))!
            .PreferredAgentId.Should().Be(AgentA);
        (await Middleware(inactivityCloseEnabled: false).RouteAsync(MakeContext(), RouteTo(Q1), CancellationToken.None))!
            .PreferredAgentId.Should().BeNull();
        _outcomes.Should().Equal(LastAgentOutcomes.Stamped, LastAgentOutcomes.NoHistory);
    }

    // ─── The real chain (design D10, audit 2026-11-06 F1) ────────────────────────────────────────────────────────

    private ServiceProvider RealChain(bool hostEnabled, Action<InboundRoutingOptions>? configure = null)
    {
        var channelConfigs = Substitute.For<ITenantChannelConfigStore>();
        var reasonHints = Substitute.For<IReasonHintResolver>();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(_conversations);
        services.AddSingleton(_queues);
        services.AddSingleton(_inactivity);
        services.AddSingleton(_clock);
        services.AddSingleton(channelConfigs);
        services.AddSingleton(reasonHints);
        services.AddInboundRouting(configure);
        services.AddSingleton(_metrics); // the test's instance, so the listener sees it
        services.Configure<LastAgentRoutingOptions>(o => o.Enabled = hostEnabled);
        return services.BuildServiceProvider();
    }

    private void DownstreamRoutesTo(Queue queue) =>
        _queues.ListAsync(Tenant, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Queue>([queue], 1, 1, 50));

    [Fact]
    public async Task RealChain_ShouldReadTheMappedQueue_WhenChannelQueueMappingOverridesADownstreamQueueThatIsOn()
    {
        var q1 = MakeQueue(Q1, lastAgent: null);
        var q2 = MakeQueue(Q2, lastAgent: true);
        _queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>()).Returns(q1);
        _queues.GetByIdAsync(Tenant, Q2, Arg.Any<CancellationToken>()).Returns(q2);
        DownstreamRoutesTo(q2);
        OwnerIs(AgentA);
        using var sp = RealChain(hostEnabled: false, o => o.ChannelQueueMapping[ChannelType.WhatsApp] = Q1);

        var result = await sp.GetRequiredService<IInboundRouter>().RouteAsync(MakeContext(), CancellationToken.None);

        result.QueueId.Should().Be(Q1, "the caller assigns the mapped queue");
        result.PreferredAgentId.Should().BeNull("the assigned queue follows the host, which is off");
        _outcomes.Should().Equal(LastAgentOutcomes.Disabled);
    }

    [Fact]
    public async Task RealChain_ShouldStamp_WhenTheMappedQueueIsOnAndTheDownstreamQueueFollowsTheHost()
    {
        var q1 = MakeQueue(Q1, lastAgent: true);
        var q2 = MakeQueue(Q2, lastAgent: null);
        _queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>()).Returns(q1);
        _queues.GetByIdAsync(Tenant, Q2, Arg.Any<CancellationToken>()).Returns(q2);
        DownstreamRoutesTo(q2);
        OwnerIs(AgentA);
        using var sp = RealChain(hostEnabled: false, o => o.ChannelQueueMapping[ChannelType.WhatsApp] = Q1);

        var result = await sp.GetRequiredService<IInboundRouter>().RouteAsync(MakeContext(), CancellationToken.None);

        result.QueueId.Should().Be(Q1);
        result.PreferredAgentId.Should().Be(AgentA);
        _outcomes.Should().Equal(LastAgentOutcomes.Stamped);
    }

    [Fact]
    public async Task RealChain_ShouldStamp_WhenBusinessHoursOverflowsFromAQueueThatFollowsTheHostToOneThatIsOn()
    {
        var closed = new HoursOfOperation("UTC"); // no schedule: always closed
        var q1 = MakeQueue(Q1, lastAgent: null, hours: closed, overflowTo: Q2);
        var q2 = MakeQueue(Q2, lastAgent: true);
        _queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>()).Returns(q1);
        _queues.GetByIdAsync(Tenant, Q2, Arg.Any<CancellationToken>()).Returns(q2);
        DownstreamRoutesTo(q1);
        OwnerIs(AgentA);
        using var sp = RealChain(hostEnabled: false);

        var result = await sp.GetRequiredService<IInboundRouter>().RouteAsync(MakeContext(), CancellationToken.None);

        result.QueueId.Should().Be(Q2);
        result.PreferredAgentId.Should().Be(AgentA);
        _outcomes.Should().Equal(LastAgentOutcomes.Stamped);
    }

    [Fact]
    public void RealChain_ShouldPlaceLastAgentJustInsideReasonHint_WhenAddInboundRoutingRegistersTheChain()
    {
        using var sp = RealChain(hostEnabled: false);

        var chain = sp.GetRequiredService<IReadOnlyList<InboundRoutingMiddlewareBase>>();

        chain.Select(m => m.GetType().Name).Should().Equal(
            nameof(ReasonHintMiddleware), nameof(LastAgentMiddleware), nameof(ChannelQueueMappingMiddleware),
            nameof(PriorityEscalationMiddleware), nameof(OverflowMiddleware), nameof(BusinessHoursMiddleware),
            nameof(DefaultQueueFallbackMiddleware));
    }
}
