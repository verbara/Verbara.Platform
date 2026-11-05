using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>The deprecated queued start records the contact's connected session, as an agent's start does (design D9).</summary>
public sealed class DefaultConversationServiceWebChatStartTests : IDisposable
{
    private const string Newer = "webchat-22222222222222222222222222222222";

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly IChannelRegistry _channels = Substitute.For<IChannelRegistry>();
    private readonly IConversationLifecycleService _lifecycle = Substitute.For<IConversationLifecycleService>();
    private readonly IChannelConnector _connector = Substitute.For<IChannelConnector, IConnectedSessionDirectory>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly TenantId _tenantId = new("tenant-1");
    private readonly EntityId _contactId = EntityId.From("contact-001");
    private readonly Contact _contact;

    public DefaultConversationServiceWebChatStartTests()
    {
        _channels.AvailableChannels.Returns([ChannelType.WebChat]);
        _channels.GetConnector(ChannelType.WebChat).Returns(_connector);
        _contact = new Contact { ContactId = _contactId, TenantId = _tenantId, CreatedAt = DateTimeOffset.UnixEpoch };
        _contacts.GetByIdAsync(_tenantId, _contactId, Arg.Any<CancellationToken>()).Returns(_contact);
    }

    public void Dispose() => _eventBus.Dispose();

    private DefaultConversationService CreateSut() =>
        new(_conversations, _contacts, Substitute.For<IMessageStore>(), _channels, _lifecycle, Substitute.For<IClock>(),
            NullLogger<DefaultConversationService>.Instance, _eventBus, Substitute.For<IWhatsAppSessionWindow>());

    [Fact]
    public async Task GetOrCreateForContactAsync_ShouldRecordTheConnectedSession_WhenTheChannelIsWebChat()
    {
        var address = new ChannelAddress(ChannelType.WebChat, Newer);
        ((IConnectedSessionDirectory)_connector).FindNewestConnectedAddressAsync(_contact, Arg.Any<CancellationToken>()).Returns(address);

        await CreateSut().GetOrCreateForContactAsync(_tenantId, _contactId, ChannelType.WebChat, CancellationToken.None);

        await _lifecycle.Received(1).CreateForWebChatSessionAsync(_tenantId, _contactId, address, Arg.Any<CancellationToken>());
        await _lifecycle.DidNotReceiveWithAnyArgs().CreateAsync(default, default!, default, default);
    }

    [Fact]
    public async Task GetOrCreateForContactAsync_ShouldRecordNothing_WhenNoSessionIsConnected()
    {
        await CreateSut().GetOrCreateForContactAsync(_tenantId, _contactId, ChannelType.WebChat, CancellationToken.None);

        await _lifecycle.Received(1).CreateAsync(_tenantId, _contactId, ChannelType.WebChat, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateForContactAsync_ShouldRecordNothing_WhenTheChannelHasNoConnector()
    {
        _channels.AvailableChannels.Returns([]);

        await CreateSut().GetOrCreateForContactAsync(_tenantId, _contactId, ChannelType.WebChat, CancellationToken.None);

        await _lifecycle.Received(1).CreateAsync(_tenantId, _contactId, ChannelType.WebChat, Arg.Any<CancellationToken>());
    }
}
