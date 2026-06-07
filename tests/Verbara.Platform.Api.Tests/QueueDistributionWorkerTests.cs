using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class QueueDistributionWorkerTests : IDisposable
{
    private const string TestTenantId = "t001";

    private readonly IConversationStore _conversationStore = Substitute.For<IConversationStore>();
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private readonly IAgentSelector _agentSelector = Substitute.For<IAgentSelector>();
    private readonly IConversationSwitchboard _switchboard = Substitute.For<IConversationSwitchboard>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IOptions<DistributionOptions> _options = Options.Create(new DistributionOptions());

    private readonly QueueDistributionWorker _sut;

    public QueueDistributionWorkerTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        _sut = new QueueDistributionWorker(
            _conversationStore,
            _tenantStore,
            _agentSelector,
            _switchboard,
            _eventBus,
            _clock,
            new Verbara.Platform.Api.Health.ServiceHeartbeat(),
            _options,
            NullLogger<QueueDistributionWorker>.Instance);
    }

    public void Dispose() => _eventBus.Dispose();

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Tenant MakeActiveTenant(string tenantId) => new()
    {
        TenantId = tenantId,
        Name = $"Tenant {tenantId}",
        Status = TenantStatus.Active,
    };

    private static Conversation MakeQueuedConversation(EntityId queueId) => new()
    {
        ConversationId = EntityId.New(),
        TenantId = new TenantId(TestTenantId),
        ContactId = EntityId.New(),
        Channel = ChannelType.WhatsApp,
        Owner = ConversationOwner.ForQueue(queueId),
        State = ConversationState.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Conversation MakeConversationWithoutOwner() => new()
    {
        ConversationId = EntityId.New(),
        TenantId = new TenantId(TestTenantId),
        ContactId = EntityId.New(),
        Channel = ChannelType.WhatsApp,
        Owner = null,
        State = ConversationState.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DistributeAsync_ShouldOfferToAgent_WhenAgentAvailable()
    {
        var queueId = EntityId.From("q1");
        var agentId = EntityId.From("a1");
        var conversation = MakeQueuedConversation(queueId);

        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { MakeActiveTenant(TestTenantId) });
        _conversationStore.ListQueuedAsync(Arg.Any<TenantId>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { conversation });
        _agentSelector.SelectAgentAsync(
                Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(),
                Arg.Any<EntityId?>(), Arg.Any<CancellationToken>())
            .Returns(agentId);
        _switchboard.OfferToAgentAsync(
                Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(new OwnershipResult(true, ConversationOwner.ForAgent(agentId), ConversationState.Offered, null));

        await _sut.DistributeAsync(CancellationToken.None);

        await _switchboard.Received(1).OfferToAgentAsync(
            conversation.ConversationId, Arg.Any<TenantId>(), agentId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DistributeAsync_ShouldSkip_WhenNoAgentsAvailable()
    {
        var conversation = MakeQueuedConversation(EntityId.From("q1"));

        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { MakeActiveTenant(TestTenantId) });
        _conversationStore.ListQueuedAsync(Arg.Any<TenantId>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { conversation });
        _agentSelector.SelectAgentAsync(
                Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(),
                Arg.Any<EntityId?>(), Arg.Any<CancellationToken>())
            .Returns((EntityId?)null);

        await _sut.DistributeAsync(CancellationToken.None);

        await _switchboard.DidNotReceive().OfferToAgentAsync(
            Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DistributeAsync_ShouldSetOfferMetadata_WhenOfferSucceeds()
    {
        var queueId = EntityId.From("q1");
        var agentId = EntityId.From("a1");
        var conversation = MakeQueuedConversation(queueId);
        var now = new DateTimeOffset(2026, 4, 8, 12, 0, 0, TimeSpan.Zero);

        _clock.UtcNow.Returns(now);
        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { MakeActiveTenant(TestTenantId) });
        _conversationStore.ListQueuedAsync(Arg.Any<TenantId>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { conversation });
        // The worker re-loads the freshly-offered conversation before stamping metadata
        // (so it stamps the Offered instance, not the stale Queued snapshot — see W5/O1).
        _conversationStore.GetByIdAsync(Arg.Any<TenantId>(), conversation.ConversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _agentSelector.SelectAgentAsync(
                Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(),
                Arg.Any<EntityId?>(), Arg.Any<CancellationToken>())
            .Returns(agentId);
        _switchboard.OfferToAgentAsync(
                Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(new OwnershipResult(true, ConversationOwner.ForAgent(agentId), ConversationState.Offered, null));

        await _sut.DistributeAsync(CancellationToken.None);

        conversation.Metadata.Should().ContainKey("_offeredAt")
            .WhoseValue.Should().Be(now.ToString("O"));
        conversation.Metadata.Should().ContainKey("_offeredTo")
            .WhoseValue.Should().Be("a1");
        // W5 — the originating queue is stamped so work-failover can re-queue back to it.
        conversation.Metadata.Should().ContainKey("originQueueId")
            .WhoseValue.Should().Be("q1");

        await _conversationStore.Received(1).SaveAsync(conversation, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DistributeAsync_ShouldSkipSuspendedTenants()
    {
        // GetAllActiveAsync already filters by Active status, so an empty list means no work
        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Tenant>());

        await _sut.DistributeAsync(CancellationToken.None);

        await _conversationStore.DidNotReceive().ListQueuedAsync(
            Arg.Any<TenantId>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DistributeAsync_ShouldSkipConversation_WhenOwnerIsNull()
    {
        var conversation = MakeConversationWithoutOwner();

        _tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { MakeActiveTenant(TestTenantId) });
        _conversationStore.ListQueuedAsync(Arg.Any<TenantId>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { conversation });

        await _sut.DistributeAsync(CancellationToken.None);

        await _agentSelector.DidNotReceive().SelectAgentAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(),
            Arg.Any<EntityId?>(), Arg.Any<CancellationToken>());
    }
}
