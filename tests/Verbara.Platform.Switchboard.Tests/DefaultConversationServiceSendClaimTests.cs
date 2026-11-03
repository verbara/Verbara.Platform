using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// conversations-close-after-inactivity task 2.4 (design D2, D9; spec <c>conversation-customer-return</c>, "An agent or
/// bot reply MUST be refused once its conversation is no longer sendable"): <c>SendCoreAsync</c> claims the row with
/// <see cref="IConversationStore.TryClaimForSendAsync"/> right before the message is stored. The claim advances the clock
/// only for a message stored <c>Pending</c> (a WhatsApp-window refusal never does), a close that commits after
/// <c>ValidateState</c> refuses the send with the existing <see cref="InvalidOperationException"/>, and a failure after
/// the claim keeps the clock it advanced.
/// </summary>
public sealed class DefaultConversationServiceSendClaimTests : IDisposable
{
    private readonly IConversationStore _conversationStore = Substitute.For<IConversationStore>();
    private readonly IContactStore _contactStore = Substitute.For<IContactStore>();
    private readonly IMessageStore _messageStore = Substitute.For<IMessageStore>();
    private readonly IChannelRegistry _channelRegistry = Substitute.For<IChannelRegistry>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IChannelConnector _connector = Substitute.For<IChannelConnector>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly IWhatsAppSessionWindow _whatsAppWindow = Substitute.For<IWhatsAppSessionWindow>();

    private readonly TenantId _tenantId = new("tenant-1");
    private readonly EntityId _conversationId = EntityId.From("conv-claim");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private readonly EntityId _botId = EntityId.From("bot-001");
    private readonly EntityId _contactId = EntityId.From("contact-001");
    private readonly DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly MessageEnvelope _envelope = new([new TextBlock("Hello!")]);

    // The store's row: GetByIdAsync hands out a copy (as Postgres materializes it), and the fake conditional update
    // matches against the row, not against the copy the service read.
    private readonly Lock _rowLock = new();
    private ConversationState _rowState = ConversationState.Active;
    private ConversationOwner? _rowOwner;
    private DateTimeOffset _rowClock;
    private Action? _afterRead;

    public DefaultConversationServiceSendClaimTests()
    {
        _rowOwner = ConversationOwner.ForAgent(_agentId);
        _rowClock = _now.AddHours(-70);
        _clock.UtcNow.Returns(_now);
        _channelRegistry.GetConnector(Arg.Any<ChannelType>()).Returns(_connector);
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(true, "ext-123", null, null));
        _whatsAppWindow.DecideAsync(default, default, default, default)
            .ReturnsForAnyArgs(new WhatsAppWindowDecision(WhatsAppSendMode.FreeForm, _now.AddMinutes(-5)));

        var contact = new Contact { ContactId = _contactId, TenantId = _tenantId, CreatedAt = _now };
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "+1234567890"));
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>()).Returns(contact);

        _conversationStore.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Conversation copy;
                lock (_rowLock)
                {
                    copy = new Conversation
                    {
                        ConversationId = _conversationId,
                        TenantId = _tenantId,
                        ContactId = _contactId,
                        Channel = ChannelType.WhatsApp,
                        State = _rowState,
                        Owner = _rowOwner,
                        CreatedAt = _now.AddDays(-5),
                        LastMessageAt = _rowClock,
                    };
                }
                var hook = _afterRead;
                _afterRead = null;
                hook?.Invoke();
                return copy;
            });

        // A faithful fake of the store's D9 statement: matches only a sendable row; moves the clock forward only when asked.
        _conversationStore.TryClaimForSendAsync(_tenantId, _conversationId, Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (_rowLock)
                {
                    if (_rowState is not (ConversationState.Active or ConversationState.OnHold or ConversationState.Consulting))
                        return false;
                    var now = call.ArgAt<DateTimeOffset>(2);
                    if (call.ArgAt<bool>(3) && _rowClock < now)
                        _rowClock = now;
                    return true;
                }
            });
    }

    public void Dispose() => _eventBus.Dispose();

    private DefaultConversationService CreateSut() =>
        new(_conversationStore, _contactStore, _messageStore, _channelRegistry,
            Substitute.For<IConversationLifecycleService>(), _clock,
            NullLogger<DefaultConversationService>.Instance, _eventBus, _whatsAppWindow);

    // The inactivity sweep's close, committing between the service's read (and so its ValidateState) and its claim.
    private void CloseCommitsAfterTheStateCheck() =>
        _afterRead = () =>
        {
            lock (_rowLock)
                _rowState = ConversationState.Closed;
        };

    private DateTimeOffset RowClock
    {
        get
        {
            lock (_rowLock)
                return _rowClock;
        }
    }

    [Fact]
    public async Task TrySendMessageAsync_ShouldRefuseAndStoreNothing_WhenTheInactivityCloseCommitsAfterTheStateCheck()
    {
        CloseCommitsAfterTheStateCheck();

        var act = () => CreateSut().TrySendMessageAsync(
            _conversationId, _tenantId, _envelope, _agentId, ConversationOwnerKind.Agent, null, CancellationToken.None);

        // The same exception ValidateState throws: the send endpoint maps it to its existing 400.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Cannot send message in conversation state*");
        await _messageStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    [Fact]
    public async Task SendMessageAsync_ShouldRefuseAndStoreNothing_WhenTheInactivityCloseCommitsAfterTheStateCheckOfABotReply()
    {
        _rowOwner = ConversationOwner.ForBot(_botId);
        CloseCommitsAfterTheStateCheck();

        var act = () => CreateSut().SendMessageAsync(
            _conversationId, _tenantId, _envelope, _botId, ConversationOwnerKind.Bot, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Cannot send message in conversation state*");
        await _messageStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    [Fact]
    public async Task TrySendMessageAsync_ShouldAdvanceTheClockBeforeStoring_WhenAnAgentReplies()
    {
        var outcome = await CreateSut().TrySendMessageAsync(
            _conversationId, _tenantId, _envelope, _agentId, ConversationOwnerKind.Agent, null, CancellationToken.None);

        outcome.IsRefused.Should().BeFalse();
        RowClock.Should().Be(_now, "an agent reply is an activity message: the claim moves the clock to the platform's now");
        Received.InOrder(() =>
        {
            _conversationStore.TryClaimForSendAsync(_tenantId, _conversationId, _now, true, Arg.Any<CancellationToken>());
            _messageStore.SaveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
            _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task SendMessageAsync_ShouldStoreFailedWithoutAdvancingTheClock_WhenTheWhatsAppWindowRefuses()
    {
        _rowOwner = ConversationOwner.ForBot(_botId);
        _whatsAppWindow.DecideAsync(_tenantId, _conversationId, null, Arg.Any<CancellationToken>())
            .Returns(new WhatsAppWindowDecision(WhatsAppSendMode.TemplateRequired, _now.AddHours(-25)));

        var message = await CreateSut().SendMessageAsync(
            _conversationId, _tenantId, _envelope, _botId, ConversationOwnerKind.Bot, CancellationToken.None);

        message.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed);
        RowClock.Should().Be(_now.AddHours(-70), "a send refused before the provider is not an activity message (D2)");
        await _conversationStore.DidNotReceive().TryClaimForSendAsync(
            _tenantId, _conversationId, Arg.Any<DateTimeOffset>(), true, Arg.Any<CancellationToken>());
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    [Fact]
    public async Task SendMessageAsync_ShouldKeepTheClockAdvanced_WhenTheConnectorFailsAfterTheClaim()
    {
        _rowOwner = ConversationOwner.ForBot(_botId);
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(false, null, "131026", "Message undeliverable"));

        var message = await CreateSut().SendMessageAsync(
            _conversationId, _tenantId, _envelope, _botId, ConversationOwnerKind.Bot, CancellationToken.None);

        message.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed);
        RowClock.Should().Be(_now, "a failure after the claim keeps the clock it advanced (D9)");
    }

    [Fact]
    public async Task SendMessageAsync_ShouldKeepTheClockAdvanced_WhenTheContactHasNoAddressAfterTheClaim()
    {
        _rowOwner = ConversationOwner.ForBot(_botId);
        _contactStore.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>())
            .Returns(new Contact { ContactId = _contactId, TenantId = _tenantId, CreatedAt = _now });

        var message = await CreateSut().SendMessageAsync(
            _conversationId, _tenantId, _envelope, _botId, ConversationOwnerKind.Bot, CancellationToken.None);

        message.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        RowClock.Should().Be(_now, "the no-address branch fails after the claim and keeps the clock (D9)");
    }
}
