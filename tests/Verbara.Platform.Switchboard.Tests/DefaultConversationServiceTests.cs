using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Switchboard.Tests;

public sealed class DefaultConversationServiceTests : IDisposable
{
    private readonly IConversationStore _conversationStore = Substitute.For<IConversationStore>();
    private readonly IContactStore _contactStore = Substitute.For<IContactStore>();
    private readonly IMessageStore _messageStore = Substitute.For<IMessageStore>();
    private readonly IChannelRegistry _channelRegistry = Substitute.For<IChannelRegistry>();
    private readonly IConversationLifecycleService _lifecycleService = Substitute.For<IConversationLifecycleService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IChannelConnector _connector = Substitute.For<IChannelConnector>();
    private readonly PlatformEventBus _eventBus = new();

    private readonly TenantId _tenantId = new("tenant-1");
    private readonly EntityId _conversationId = EntityId.From("conv-001");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private readonly EntityId _botId = EntityId.From("bot-001");
    private readonly EntityId _contactId = EntityId.From("contact-001");
    private readonly DateTimeOffset _now = new(2026, 3, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly MessageEnvelope _envelope = new([new TextBlock("Hello!")]);

    public DefaultConversationServiceTests()
    {
        _clock.UtcNow.Returns(_now);
        _channelRegistry.GetConnector(Arg.Any<ChannelType>()).Returns(_connector);
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(true, "ext-123", null, null));
    }

    public void Dispose() => _eventBus.Dispose();

    private DefaultConversationService CreateSut() =>
        new(_conversationStore, _contactStore, _messageStore, _channelRegistry,
            _lifecycleService, _clock, NullLogger<DefaultConversationService>.Instance, _eventBus);

    private Conversation BuildConversation(
        ConversationState state = ConversationState.Active,
        ConversationOwner? owner = null)
        => new()
        {
            ConversationId = _conversationId,
            TenantId = _tenantId,
            ContactId = _contactId,
            Channel = ChannelType.WhatsApp,
            State = state,
            Owner = owner,
            CreatedAt = _now,
        };

    private Contact BuildContact(ChannelAddress? address = null)
    {
        var contact = new Contact
        {
            ContactId = _contactId,
            TenantId = _tenantId,
            CreatedAt = _now,
        };
        if (address is not null)
            contact.AddAddress(address);
        return contact;
    }

    private static ChannelAddress WhatsAppAddress() =>
        new(ChannelType.WhatsApp, "+1234567890");

    // ─── SendMessageAsync — persistence ──────────────────────────────────────

    [Fact]
    public async Task SendMessageAsync_ShouldPersistMessage_WithOutboundDirection()
    {
        var conversation = BuildConversation(owner: ConversationOwner.System);
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(BuildContact(WhatsAppAddress()));

        var sut = CreateSut();
        var result = await sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            EntityId.From("system"), ConversationOwnerKind.System, CancellationToken.None);

        result.Direction.Should().Be(MessageDirection.Outbound);
        await _messageStore.Received(1).SaveAsync(
            Arg.Is<Message>(m => m.Direction == MessageDirection.Outbound), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessageAsync_ShouldCallChannelConnector_WithCorrectAddress()
    {
        var address = WhatsAppAddress();
        var conversation = BuildConversation(owner: ConversationOwner.System);
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(BuildContact(address));

        var sut = CreateSut();
        await sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            EntityId.From("system"), ConversationOwnerKind.System, CancellationToken.None);

        await _connector.Received(1).SendAsync(
            Arg.Is<OutboundMessage>(m => m.To == address && m.Content == _envelope),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessageAsync_ShouldUpdateDeliveryStatus_OnSuccess()
    {
        var conversation = BuildConversation(owner: ConversationOwner.System);
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(BuildContact(WhatsAppAddress()));
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(true, "ext-msg-999", null, null));

        var sut = CreateSut();
        var result = await sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            EntityId.From("system"), ConversationOwnerKind.System, CancellationToken.None);

        result.DeliveryStatus.Should().Be(MessageDeliveryStatus.Sent);
        await _messageStore.Received(1).UpdateDeliveryStatusAsync(
            _tenantId, result.MessageId, MessageDeliveryStatus.Sent, _now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessageAsync_ShouldSetFailedStatus_OnSendFailure()
    {
        var conversation = BuildConversation(owner: ConversationOwner.System);
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(BuildContact(WhatsAppAddress()));
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(false, null, "RATE_LIMITED", "Too many requests"));

        var sut = CreateSut();
        var result = await sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            EntityId.From("system"), ConversationOwnerKind.System, CancellationToken.None);

        result.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed);
        await _messageStore.Received(1).UpdateDeliveryStatusAsync(
            _tenantId, result.MessageId, MessageDeliveryStatus.Failed, null, Arg.Any<CancellationToken>());
    }

    // ─── SendMessageAsync — error cases ──────────────────────────────────────

    [Fact]
    public async Task SendMessageAsync_ShouldThrow_WhenConversationNotFound()
    {
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);

        var sut = CreateSut();
        var act = () => sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            _agentId, ConversationOwnerKind.Agent, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task SendMessageAsync_ShouldThrow_WhenAgentNotOwner()
    {
        var otherAgent = EntityId.From("agent-999");
        var conversation = BuildConversation(owner: ConversationOwner.ForAgent(otherAgent));
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);

        var sut = CreateSut();
        var act = () => sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            _agentId, ConversationOwnerKind.Agent, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not the owner*");
    }

    [Fact]
    public async Task SendMessageAsync_ShouldThrow_WhenConversationStateIsResolved()
    {
        var conversation = BuildConversation(ConversationState.Resolved, ConversationOwner.System);
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);

        var sut = CreateSut();
        var act = () => sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            EntityId.From("system"), ConversationOwnerKind.System, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*state*");
    }

    [Fact]
    public async Task SendMessageAsync_ShouldAllowSystem_WithoutOwnershipCheck()
    {
        // Conversation owned by an agent — system can still send
        var conversation = BuildConversation(owner: ConversationOwner.ForAgent(_agentId));
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(BuildContact(WhatsAppAddress()));

        var sut = CreateSut();
        var act = () => sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            EntityId.From("system"), ConversationOwnerKind.System, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendMessageAsync_ShouldAllowBot_WhenBotIsOwner()
    {
        var conversation = BuildConversation(owner: ConversationOwner.ForBot(_botId));
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(BuildContact(WhatsAppAddress()));

        var sut = CreateSut();
        var act = () => sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            _botId, ConversationOwnerKind.Bot, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendMessageAsync_ShouldThrow_WhenBotNotOwner()
    {
        var conversation = BuildConversation(owner: ConversationOwner.ForAgent(_agentId));
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);

        var sut = CreateSut();
        var act = () => sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            _botId, ConversationOwnerKind.Bot, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not owned by a bot*");
    }

    [Fact]
    public async Task SendMessageAsync_ShouldNotCallConnector_WhenContactHasNoAddress()
    {
        var conversation = BuildConversation(owner: ConversationOwner.System);
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        // Contact with no WhatsApp address
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(BuildContact());

        var sut = CreateSut();
        var result = await sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            EntityId.From("system"), ConversationOwnerKind.System, CancellationToken.None);

        await _connector.DidNotReceive().SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
        result.DeliveryStatus.Should().Be(MessageDeliveryStatus.Pending);
    }

    [Fact]
    public async Task SendMessageAsync_ShouldAllowSend_WhenStateIsOnHold()
    {
        var conversation = BuildConversation(ConversationState.OnHold, ConversationOwner.ForAgent(_agentId));
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(BuildContact(WhatsAppAddress()));

        var sut = CreateSut();
        var act = () => sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            _agentId, ConversationOwnerKind.Agent, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendMessageAsync_ShouldAllowSend_WhenStateIsConsulting()
    {
        var conversation = BuildConversation(ConversationState.Consulting, ConversationOwner.ForAgent(_agentId));
        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(BuildContact(WhatsAppAddress()));

        var sut = CreateSut();
        var act = () => sut.SendMessageAsync(
            _conversationId, _tenantId, _envelope,
            _agentId, ConversationOwnerKind.Agent, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ─── GetOrCreateForContactAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateForContactAsync_ShouldReturnExisting_WhenActiveConversationExists()
    {
        var existing = BuildConversation();
        _conversationStore.FindActiveByContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(existing);

        var sut = CreateSut();
        var result = await sut.GetOrCreateForContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, CancellationToken.None);

        result.Should().BeSameAs(existing);
        await _lifecycleService.DidNotReceive().CreateAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateForContactAsync_ShouldCreateNew_WhenNoActiveConversation()
    {
        _conversationStore.FindActiveByContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);

        var newConversation = BuildConversation();
        _lifecycleService.CreateAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(newConversation);

        var sut = CreateSut();
        var result = await sut.GetOrCreateForContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, CancellationToken.None);

        result.Should().BeSameAs(newConversation);
        await _lifecycleService.Received(1).CreateAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>());
    }

    // ─── GetOrCreateForContactAsync — tenant isolation (regression, v1.9.0) ──

    [Fact]
    public async Task GetOrCreateForContactAsync_ShouldQueryStore_WithCallerTenantId()
    {
        // Regression: outbound conversation creation must scope every storage lookup
        // to the caller's tenant. A tenant-context gap here would allow cross-tenant
        // reuse of conversations and/or silent misattribution on create.
        _conversationStore.FindActiveByContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        _lifecycleService.CreateAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(BuildConversation());

        var sut = CreateSut();
        await sut.GetOrCreateForContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, CancellationToken.None);

        // FindActive must receive the caller's tenantId (not default/empty/other).
        await _conversationStore.Received(1).FindActiveByContactAsync(
            _tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>());
        await _conversationStore.DidNotReceive().FindActiveByContactAsync(
            Arg.Is<TenantId>(t => t != _tenantId), Arg.Any<EntityId>(),
            Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateForContactAsync_ShouldInvokeLifecycle_WithCallerTenantId()
    {
        // Regression: newly created conversation must be attributed to the caller's
        // tenant. DefaultConversationLifecycleService.CreateAsync stamps Conversation.TenantId
        // from its tenantId parameter, so a gap here means cross-tenant writes.
        _conversationStore.FindActiveByContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        _lifecycleService.CreateAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(BuildConversation());

        var sut = CreateSut();
        await sut.GetOrCreateForContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, CancellationToken.None);

        await _lifecycleService.Received(1).CreateAsync(
            _tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>());
        await _lifecycleService.DidNotReceive().CreateAsync(
            Arg.Is<TenantId>(t => t != _tenantId), Arg.Any<EntityId>(),
            Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateForContactAsync_ShouldNotReuseConversation_FromDifferentTenant()
    {
        // Regression: a tenant-scoped store returns null when the caller's tenant has no
        // active conversation, even if another tenant has one for the same contactId.
        // The service must then fall through to CreateAsync with the caller's tenant —
        // it MUST NOT return the foreign-tenant conversation.
        var tenantA = new TenantId("tenant-A");
        var tenantB = new TenantId("tenant-B");
        var tenantBConversation = new Conversation
        {
            ConversationId = EntityId.From("conv-tenant-B"),
            TenantId = tenantB,
            ContactId = _contactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Active,
            CreatedAt = _now,
        };

        // Properly tenant-scoped store: tenant A lookup returns null even though tenant B has a match.
        _conversationStore.FindActiveByContactAsync(tenantA, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        _conversationStore.FindActiveByContactAsync(tenantB, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(tenantBConversation);

        var tenantAConversation = new Conversation
        {
            ConversationId = EntityId.From("conv-tenant-A"),
            TenantId = tenantA,
            ContactId = _contactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued,
            CreatedAt = _now,
        };
        _lifecycleService.CreateAsync(tenantA, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(tenantAConversation);

        var sut = CreateSut();
        var result = await sut.GetOrCreateForContactAsync(tenantA, _contactId, ChannelType.WhatsApp, CancellationToken.None);

        // The returned conversation belongs to tenant A, not tenant B.
        result.TenantId.Should().Be(tenantA);
        result.ConversationId.Value.Should().Be("conv-tenant-A");
        result.Should().NotBeSameAs(tenantBConversation);
        await _lifecycleService.Received(1).CreateAsync(
            tenantA, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>());
    }
}
