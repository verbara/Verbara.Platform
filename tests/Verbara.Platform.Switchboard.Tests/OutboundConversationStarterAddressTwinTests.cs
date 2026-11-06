using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// contacts-writes-follow-their-permissions, review round: a new start is refused when any contact of the tenant that
/// holds the destination address on the channel is marked do-not-contact or records its consent there as no, not only
/// the contact the request names (design D11, D13). A duplicate contact holding the same number is otherwise messaged
/// through its unmarked twin. The reuse of the caller's own open, sendable conversation is still not refused.
/// </summary>
public sealed class OutboundConversationStarterAddressTwinTests : IDisposable
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
    private readonly EntityId _contactId = EntityId.From("contact-named");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private const string ActorUserId = "user-of-agent-001";
    private const string Address = "+15550001111";
    private readonly DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public OutboundConversationStarterAddressTwinTests()
    {
        _clock.UtcNow.Returns(_now);
        _channels.AvailableChannels.Returns([ChannelType.WhatsApp, ChannelType.Sms]);
        _channels.GetConnector(Arg.Any<ChannelType>()).Returns(_connector);
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(true, "out-1", null, null));
        _capacity.TryReserveAsync(_tenantId, _agentId, Arg.Any<ChannelType>(), Arg.Any<CancellationToken>()).Returns(true);
        _conversations.FindLastInboundAtAsync(_tenantId, _contactId, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(_now.AddHours(-1));
        _conversations.TryClaimForSendAsync(default, default, default, default, default).ReturnsForAnyArgs(true);
    }

    public void Dispose() => _eventBus.Dispose();

    private OutboundConversationStarter CreateSut() =>
        new(_conversations, _contacts, _messages, _channels, _capacity, _clock, _eventBus,
            NullLogger<OutboundConversationStarter>.Instance);

    private OutboundStartRequest Request(ChannelType channel) =>
        new(_tenantId, _contactId, channel, _agentId, ActorUserId, new MessageEnvelope([new TextBlock("Hello")]));

    private Contact NewContact(EntityId id, ChannelType channel)
    {
        var contact = new Contact { ContactId = id, TenantId = _tenantId, CreatedAt = _now };
        contact.AddAddress(new ChannelAddress(channel, Address));
        return contact;
    }

    /// <summary>The named contact, unmarked, and a twin holding the same address on <paramref name="channel"/>.</summary>
    private Contact SeedTwins(ChannelType channel, Action<Contact> restrictTwin)
    {
        var named = NewContact(_contactId, channel);
        var twin = NewContact(EntityId.From("contact-twin"), channel);
        restrictTwin(twin);
        _contacts.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>()).Returns(named);
        _contacts.FindAllByChannelAddressAsync(_tenantId, channel, Address, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([twin, named]);
        return twin;
    }

    public static TheoryData<ChannelType, bool> Cases() => new()
    {
        { ChannelType.WhatsApp, true },
        { ChannelType.WhatsApp, false },
        { ChannelType.Sms, true },
        { ChannelType.Sms, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task StartAsync_ShouldRefuseAndSendNothing_WhenATwinHoldingTheAddressIsRestricted(ChannelType channel, bool doNotContact)
    {
        SeedTwins(channel, twin =>
        {
            if (doNotContact)
                twin.DoNotContact = true;
            else
                twin.SetConsent(channel, false);
        });

        var outcome = await CreateSut().StartAsync(Request(channel), CancellationToken.None);

        outcome.RefusalCode.Should().Be(doNotContact ? OutboundStartCodes.ContactDoNotContact : OutboundStartCodes.ContactConsentWithdrawn);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        await _conversations.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _messages.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _capacity.DidNotReceiveWithAnyArgs().TryReserveAsync(default, default, default, default);
    }

    [Theory]
    [InlineData(ChannelType.WhatsApp)]
    [InlineData(ChannelType.Sms)]
    public async Task StartAsync_ShouldStart_WhenTheTwinRecordsNoOnlyOnAnotherChannel(ChannelType channel)
    {
        SeedTwins(channel, twin => twin.SetConsent(ChannelType.Voice, false));

        var outcome = await CreateSut().StartAsync(Request(channel), CancellationToken.None);

        outcome.IsRefused.Should().BeFalse(outcome.RefusalCode);
        await _connector.Received(1).SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ChannelType.WhatsApp)]
    [InlineData(ChannelType.Sms)]
    public async Task StartAsync_ShouldReuseTheOwnOpenConversation_WhenATwinHoldingTheAddressIsMarked(ChannelType channel)
    {
        SeedTwins(channel, twin => twin.DoNotContact = true);
        var open = new Conversation
        {
            ConversationId = EntityId.From("conv-open"),
            TenantId = _tenantId,
            ContactId = _contactId,
            Channel = channel,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(_agentId),
            CreatedAt = _now.AddHours(-2),
        };
        _conversations.FindActiveByContactAsync(_tenantId, _contactId, channel, Arg.Any<CancellationToken>()).Returns(open);

        var outcome = await CreateSut().StartAsync(Request(channel), CancellationToken.None);

        outcome.IsRefused.Should().BeFalse(outcome.RefusalCode);
        outcome.Reused.Should().BeTrue();
        await _connector.Received(1).SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
    }
}
