using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// An agent's WebChat start goes to a session that can receive it and records that session on the conversation, in the
/// insert (webchat-visitor-identity-two-levels, design D9).
/// </summary>
public sealed class OutboundConversationStarterWebChatTests : IDisposable
{
    private const string Older = "webchat-11111111111111111111111111111111";
    private const string Newer = "webchat-22222222222222222222222222222222";

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly IMessageStore _messages = Substitute.For<IMessageStore>();
    private readonly IChannelRegistry _channels = Substitute.For<IChannelRegistry>();
    private readonly IChannelConnector _connector = Substitute.For<IChannelConnector, IConnectedSessionDirectory>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly PlatformEventBus _eventBus = new();

    private readonly TenantId _tenantId = new("tenant-1");
    private readonly EntityId _contactId = EntityId.From("contact-001");
    private readonly EntityId _agentId = EntityId.From("agent-001");
    private readonly DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly Contact _contact;
    private string? _recordedWhenSaved;

    public OutboundConversationStarterWebChatTests()
    {
        _clock.UtcNow.Returns(_now);
        _channels.AvailableChannels.Returns([ChannelType.WebChat, ChannelType.WhatsApp]);
        _channels.GetConnector(Arg.Any<ChannelType>()).Returns(_connector);
        _connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>()).Returns(new SendResult(true, "ext-1", null, null));
        _capacity.TryReserveAsync(default, default!, default, default).ReturnsForAnyArgs(true);
        _conversations.TryClaimForSendAsync(default, default, default, default, default).ReturnsForAnyArgs(true);
        _conversations.SaveAsync(
            Arg.Do<Conversation>(c => _recordedWhenSaved = c.Metadata.GetValueOrDefault(WebChatConversationMetadata.SessionAddressKey)),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _contact = new Contact { ContactId = _contactId, TenantId = _tenantId, CreatedAt = _now };
        _contact.AddAddress(new ChannelAddress(ChannelType.WebChat, Older));
        _contact.AddAddress(new ChannelAddress(ChannelType.WebChat, Newer));
        _contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "+15550001111"));
        _contacts.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>()).Returns(_contact);
        _conversations.FindLastInboundAtAsync(default, default, default, default).ReturnsForAnyArgs(_now.AddHours(-1));
    }

    public void Dispose() => _eventBus.Dispose();

    private OutboundConversationStarter CreateSut() =>
        new(_conversations, _contacts, _messages, _channels, _capacity, _clock, _eventBus,
            NullLogger<OutboundConversationStarter>.Instance);

    private OutboundStartRequest Request(ChannelType channel = ChannelType.WebChat) =>
        new(_tenantId, _contactId, channel, _agentId, "user-1", new MessageEnvelope([new TextBlock("Hello")]));

    private void Connected(string? address) =>
        ((IConnectedSessionDirectory)_connector).FindNewestConnectedAddressAsync(_contact, Arg.Any<CancellationToken>())
            .Returns(address is null ? null : new ChannelAddress(ChannelType.WebChat, address));

    private async Task SentTo(string address) =>
        await _connector.Received(1).SendAsync(Arg.Is<OutboundMessage>(m => m.To.Address == address), Arg.Any<CancellationToken>());

    private Conversation OpenOwnedByTheCaller(string? recorded)
    {
        var open = new Conversation
        {
            ConversationId = EntityId.From("conv-open"),
            TenantId = _tenantId,
            ContactId = _contactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(_agentId),
            CreatedAt = _now.AddHours(-1),
        };
        if (recorded is not null)
            open.SetMetadata(WebChatConversationMetadata.SessionAddressKey, recorded);
        _conversations.FindActiveByContactAsync(_tenantId, _contactId, ChannelType.WebChat, Arg.Any<CancellationToken>()).Returns(open);
        return open;
    }

    [Fact]
    public async Task StartAsync_ShouldSendToTheConnectedSessionAndRecordItInTheInsert_WhenTheConversationIsNew()
    {
        Connected(Newer);

        var outcome = await CreateSut().StartAsync(Request(), CancellationToken.None);

        outcome.RefusalCode.Should().BeNull();
        await SentTo(Newer);
        _recordedWhenSaved.Should().Be(Newer);
    }

    [Fact]
    public async Task StartAsync_ShouldKeepTodaysDestination_WhenNoSessionIsConnected()
    {
        Connected(null);

        await CreateSut().StartAsync(Request(), CancellationToken.None);

        await SentTo(Older);
    }

    [Fact]
    public async Task StartAsync_ShouldSendToTheConversationsOwnSession_WhenItReusesOne()
    {
        OpenOwnedByTheCaller(Newer);
        Connected(Older);

        var outcome = await CreateSut().StartAsync(Request(), CancellationToken.None);

        outcome.Reused.Should().BeTrue();
        await SentTo(Newer);
    }

    [Fact]
    public async Task StartAsync_ShouldSendToTheContactsAddress_WhenTheReusedConversationRecordsNoSession()
    {
        OpenOwnedByTheCaller(recorded: null);
        Connected(Newer);

        await CreateSut().StartAsync(Request(), CancellationToken.None);

        await SentTo(Older);
    }

    [Fact]
    public async Task StartAsync_ShouldRecordNothingAndAskNoDirectory_WhenTheChannelIsNotWebChat()
    {
        await CreateSut().StartAsync(Request(ChannelType.WhatsApp), CancellationToken.None);

        await SentTo("+15550001111");
        _recordedWhenSaved.Should().BeNull();
        await ((IConnectedSessionDirectory)_connector).DidNotReceiveWithAnyArgs().FindNewestConnectedAddressAsync(default!, default);
    }
}
