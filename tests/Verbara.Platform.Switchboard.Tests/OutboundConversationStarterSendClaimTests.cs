using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// conversations-close-after-inactivity task 2.4 (design D2, D9; spec <c>conversation-customer-return</c>): an agent
/// start that creates its conversation births it with the clock at the send time; a start that reuses the agent's own
/// open conversation runs <see cref="IConversationStore.TryClaimForSendAsync"/> after the existing refusals and before
/// the provider send, and a close that commits after its <c>IsSendable</c> check refuses it with
/// <see cref="OutboundStartCodes.ConversationNotSendable"/>, sending and storing nothing.
/// </summary>
public sealed class OutboundConversationStarterSendClaimTests : IDisposable
{
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly IMessageStore _messages = Substitute.For<IMessageStore>();
    private readonly IChannelRegistry _channels = Substitute.For<IChannelRegistry>();
    private readonly IChannelConnector _connector = Substitute.For<IChannelConnector>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly PlatformEventBus _eventBus = new();

    private readonly TenantId _tenantId = new("tenant-1");
    private readonly EntityId _contactId = EntityId.From("contact-001");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private readonly EntityId _openId = EntityId.From("conv-open");
    private readonly DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    // The store's row of the agent's open conversation; FindActive hands out a copy, the fake claim matches the row.
    private readonly Lock _rowLock = new();
    private ConversationState _rowState = ConversationState.Active;
    private DateTimeOffset _rowClock;
    private bool _open;

    public OutboundConversationStarterSendClaimTests()
    {
        _rowClock = _now.AddHours(-70);
        _clock.UtcNow.Returns(_now);
        _channels.AvailableChannels.Returns([ChannelType.WhatsApp]);
        _channels.GetConnector(Arg.Any<ChannelType>()).Returns(_connector);
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(true, "wamid.out-1", null, null));
        _capacity.TryReserveAsync(_tenantId, _agentId, Arg.Any<ChannelType>(), Arg.Any<CancellationToken>()).Returns(true);
        _conversations.FindLastInboundAtAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(_now.AddHours(-1));

        var contact = new Contact { ContactId = _contactId, TenantId = _tenantId, CreatedAt = _now };
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "+15550001111"));
        _contacts.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>()).Returns(contact);

        _conversations.FindActiveByContactAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (!_open)
                    return (Conversation?)null;
                lock (_rowLock)
                {
                    return new Conversation
                    {
                        ConversationId = _openId,
                        TenantId = _tenantId,
                        ContactId = _contactId,
                        Channel = ChannelType.WhatsApp,
                        State = _rowState,
                        Owner = ConversationOwner.ForAgent(_agentId),
                        CreatedAt = _now.AddDays(-3),
                        LastMessageAt = _rowClock,
                    };
                }
            });

        // A faithful fake of the store's D9 statement.
        _conversations.TryClaimForSendAsync(_tenantId, _openId, Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
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

    private OutboundConversationStarter CreateSut() =>
        new(_conversations, _contacts, _messages, _channels, _capacity, _clock, _eventBus,
            NullLogger<OutboundConversationStarter>.Instance);

    private OutboundStartRequest Request() =>
        new(_tenantId, _contactId, ChannelType.WhatsApp, _agentId, "user-of-agent-001",
            new MessageEnvelope([new TextBlock("Hello from your agent")]), null);

    private DateTimeOffset RowClock
    {
        get
        {
            lock (_rowLock)
                return _rowClock;
        }
    }

    [Fact]
    public async Task StartAsync_ShouldBirthTheConversationWithTheClockAtTheSendTime_WhenItCreatesOne()
    {
        var outcome = await CreateSut().StartAsync(Request(), CancellationToken.None);

        outcome.IsRefused.Should().BeFalse();
        outcome.Reused.Should().BeFalse();
        await _conversations.Received(1).SaveAsync(
            Arg.Is<Conversation>(c => c.LastMessageAt == _now), CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldClaimBeforeTheProviderSendAndAdvanceTheClock_WhenItReusesTheAgentsOwnConversation()
    {
        _open = true;

        var outcome = await CreateSut().StartAsync(Request(), CancellationToken.None);

        outcome.IsRefused.Should().BeFalse();
        outcome.Reused.Should().BeTrue();
        RowClock.Should().Be(_now, "the reuse's message is an activity message: the claim moves the clock to now");
        Received.InOrder(() =>
        {
            _conversations.TryClaimForSendAsync(_tenantId, _openId, _now, true, Arg.Any<CancellationToken>());
            _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task StartAsync_ShouldRefuseNotSendableAndSendNothing_WhenTheInactivityCloseCommitsBeforeTheReuseClaim()
    {
        _open = true;
        // The window read runs after the IsSendable check: the sweep's close commits there, before the claim.
        _conversations.FindLastInboundAtAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                lock (_rowLock)
                    _rowState = ConversationState.Closed;
                return (DateTimeOffset?)_now.AddHours(-1);
            });

        var outcome = await CreateSut().StartAsync(Request(), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.ConversationNotSendable);
        outcome.OpenConversationId.Should().Be(_openId);
        outcome.OpenOwnerKind.Should().Be(ConversationOwnerKind.Agent);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await _messages.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _conversations.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _capacity.DidNotReceiveWithAnyArgs().TryReserveAsync(default, default, default, default);
    }

    [Fact]
    public async Task StartAsync_ShouldKeepTheClockAdvanced_WhenTheProviderRejectsTheReusedSendAfterTheClaim()
    {
        _open = true;
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(false, null, "131026", "Message undeliverable"));

        var outcome = await CreateSut().StartAsync(Request(), CancellationToken.None);

        outcome.RefusalCode.Should().Be(OutboundStartCodes.ChannelSendFailed);
        RowClock.Should().Be(_now, "D9: a send that fails after the claim keeps the clock it advanced");
        await _messages.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }
}
