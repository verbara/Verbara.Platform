using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Switchboard;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

public sealed class ConversationTimeoutWorkerTests : IDisposable
{
    private const string TestTenantId = "t001";

    private readonly IConversationStore _conversationStore = Substitute.For<IConversationStore>();
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private readonly IConversationSwitchboard _switchboard = Substitute.For<IConversationSwitchboard>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IOptions<DistributionOptions> _options = Options.Create(new DistributionOptions());

    private readonly DateTimeOffset _now = new(2026, 4, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly ConversationTimeoutWorker _sut;

    public ConversationTimeoutWorkerTests()
    {
        _clock.UtcNow.Returns(_now);

        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { MakeActiveTenant(TestTenantId) });

        _switchboard.RejectAsync(
                Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(new OwnershipResult(true, ConversationOwner.ForQueue(EntityId.From("q1")), ConversationState.Queued, null));

        _sut = new ConversationTimeoutWorker(
            _conversationStore,
            _tenantStore,
            _switchboard,
            _eventBus,
            _clock,
            new Asterisk.Platform.Api.Health.ServiceHeartbeat(),
            _options,
            NullLogger<ConversationTimeoutWorker>.Instance);
    }

    public void Dispose() => _eventBus.Dispose();

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Tenant MakeActiveTenant(string tenantId) => new()
    {
        TenantId = tenantId,
        Name = $"Tenant {tenantId}",
        Status = TenantStatus.Active,
    };

    private static Conversation MakeOfferedConversation(DateTimeOffset offeredAt, string agentId = "a1")
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = new TenantId(TestTenantId),
            ContactId = EntityId.New(),
            Channel = ChannelType.WhatsApp,
            Owner = ConversationOwner.ForAgent(EntityId.From(agentId)),
            State = ConversationState.Offered,
            CreatedAt = offeredAt.AddSeconds(-5),
        };
        conv.SetMetadata("_offeredAt", offeredAt.ToString("O"));
        conv.SetMetadata("_offeredTo", agentId);
        return conv;
    }

    private static Conversation MakeQueuedConversation(DateTimeOffset createdAt)
    {
        return new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = new TenantId(TestTenantId),
            ContactId = EntityId.New(),
            Channel = ChannelType.WhatsApp,
            Owner = ConversationOwner.ForQueue(EntityId.From("q1")),
            State = ConversationState.Queued,
            CreatedAt = createdAt,
        };
    }

    private static Conversation MakeWrapUpConversation(DateTimeOffset updatedAt)
    {
        return new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = new TenantId(TestTenantId),
            ContactId = EntityId.New(),
            Channel = ChannelType.WhatsApp,
            Owner = ConversationOwner.ForAgent(EntityId.From("a1")),
            State = ConversationState.WrapUp,
            CreatedAt = updatedAt.AddMinutes(-10),
            UpdatedAt = updatedAt,
        };
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldRejectExpiredOffer_WhenOfferTimedOut()
    {
        // Offered 60s ago, timeout is 30s → should expire
        var conv = MakeOfferedConversation(_now.AddSeconds(-60));

        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Offered, 100, Arg.Any<CancellationToken>())
            .Returns(new[] { conv });
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Queued, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.WrapUp, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());

        await _sut.ProcessTimeoutsAsync(CancellationToken.None);

        await _switchboard.Received(1).RejectAsync(
            conv.ConversationId, Arg.Any<TenantId>(), EntityId.From("a1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldNotReject_WhenOfferNotExpired()
    {
        // Offered 10s ago, timeout is 30s → should NOT expire
        var conv = MakeOfferedConversation(_now.AddSeconds(-10));

        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Offered, 100, Arg.Any<CancellationToken>())
            .Returns(new[] { conv });
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Queued, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.WrapUp, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());

        await _sut.ProcessTimeoutsAsync(CancellationToken.None);

        await _switchboard.DidNotReceive().RejectAsync(
            Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldAbandon_WhenQueueTimedOut()
    {
        // Created 400s ago, timeout is 300s → should abandon
        var conv = MakeQueuedConversation(_now.AddSeconds(-400));

        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Offered, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Queued, 100, Arg.Any<CancellationToken>())
            .Returns(new[] { conv });
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.WrapUp, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());

        await _sut.ProcessTimeoutsAsync(CancellationToken.None);

        conv.State.Should().Be(ConversationState.Abandoned);
        conv.UpdatedAt.Should().Be(_now);
        await _conversationStore.Received(1).SaveAsync(conv, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldCloseWrapUp_WhenTimedOut()
    {
        // UpdatedAt 200s ago, timeout is 120s → should close
        var conv = MakeWrapUpConversation(_now.AddSeconds(-200));

        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Offered, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Queued, 100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Conversation>());
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.WrapUp, 100, Arg.Any<CancellationToken>())
            .Returns(new[] { conv });

        await _sut.ProcessTimeoutsAsync(CancellationToken.None);

        conv.State.Should().Be(ConversationState.Closed);
        conv.ClosedAt.Should().Be(_now);
        conv.UpdatedAt.Should().Be(_now);
        await _conversationStore.Received(1).SaveAsync(conv, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldPublishEvents_WhenTransitioning()
    {
        var offeredConv = MakeOfferedConversation(_now.AddSeconds(-60));
        var queuedConv = MakeQueuedConversation(_now.AddSeconds(-400));
        var wrapUpConv = MakeWrapUpConversation(_now.AddSeconds(-200));

        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Offered, 100, Arg.Any<CancellationToken>())
            .Returns(new[] { offeredConv });
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Queued, 100, Arg.Any<CancellationToken>())
            .Returns(new[] { queuedConv });
        _conversationStore.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.WrapUp, 100, Arg.Any<CancellationToken>())
            .Returns(new[] { wrapUpConv });

        var events = new List<PlatformEvent>();
        using var sub = _eventBus.Events.Subscribe(events.Add);

        await _sut.ProcessTimeoutsAsync(CancellationToken.None);

        events.Should().HaveCount(3);
        events.Should().ContainSingle(e => e is ConversationOfferExpiredEvent);
        events.Should().ContainSingle(e => e is ConversationAbandonedEvent);
        events.Should().ContainSingle(e => e is ConversationStateChangedEvent);
    }
}
