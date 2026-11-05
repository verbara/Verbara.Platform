using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat.Tests;

/// <summary>Which of a contact's sessions has a visitor connected now: what an agent's new conversation is sent to.</summary>
public sealed class WebChatConnectorConnectedSessionTests
{
    private const string Older = "webchat-11111111111111111111111111111111";
    private const string Newer = "webchat-22222222222222222222222222222222";

    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId ContactId = EntityId.From("contact-1");

    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly WebChatSessionManager _manager = new(Options.Create(new WebChatOptions()));
    private readonly WebChatConnector _sut;

    public WebChatConnectorConnectedSessionTests()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        _sut = new WebChatConnector(_manager, Substitute.For<IWebChatTransport>());
    }

    private static Contact ContactHolding(params string[] addresses)
    {
        var contact = new Contact { ContactId = ContactId, TenantId = Tenant, CreatedAt = DateTimeOffset.UnixEpoch };
        foreach (var address in addresses)
            contact.AddAddress(new ChannelAddress(ChannelType.WebChat, address));
        return contact;
    }

    private async Task<string> SessionAsync(string address, TenantId? tenant = null, EntityId? contact = null, bool attached = true)
    {
        var sessionId = await _manager.ConnectAsync(
            tenant ?? Tenant, contact ?? ContactId, EntityId.From("conv-1"), new ChannelAddress(ChannelType.WebChat, address), _clock);
        if (attached)
            await _manager.AttachSocketAsync(sessionId, _clock);
        return sessionId;
    }

    private Task<ChannelAddress?> FindAsync(Contact contact) => _sut.FindNewestConnectedAddressAsync(contact, CancellationToken.None);

    [Fact]
    public async Task FindNewestConnectedAddressAsync_ShouldReturnTheNewest_WhenTwoAreConnected()
    {
        await SessionAsync(Older);
        await SessionAsync(Newer);

        (await FindAsync(ContactHolding(Older, Newer)))!.Address.Should().Be(Newer);
    }

    [Fact]
    public async Task FindNewestConnectedAddressAsync_ShouldSkipASessionWithNoSocket_WhenAnOlderOneHasOne()
    {
        await SessionAsync(Older);
        await SessionAsync(Newer, attached: false);

        (await FindAsync(ContactHolding(Older, Newer)))!.Address.Should().Be(Older);
    }

    [Fact]
    public async Task FindNewestConnectedAddressAsync_ShouldReturnNull_WhenTheLastSocketDetached()
    {
        var sessionId = await SessionAsync(Newer);
        await _manager.DetachSocketAsync(sessionId);

        (await FindAsync(ContactHolding(Newer))).Should().BeNull();
    }

    [Fact]
    public async Task FindNewestConnectedAddressAsync_ShouldReturnNull_WhenTheSessionIsAnotherTenants()
    {
        await SessionAsync(Newer, tenant: new TenantId("tenant-2"));

        (await FindAsync(ContactHolding(Newer))).Should().BeNull();
    }

    [Fact]
    public async Task FindNewestConnectedAddressAsync_ShouldReturnNull_WhenTheSessionIsAnotherContacts()
    {
        await SessionAsync(Newer, contact: EntityId.From("contact-2"));

        (await FindAsync(ContactHolding(Newer))).Should().BeNull();
    }

    [Fact]
    public async Task FindNewestConnectedAddressAsync_ShouldReturnNull_WhenTheContactHoldsOnlyAnIdentityKey()
    {
        (await FindAsync(ContactHolding(ChannelAddress.WebChatExternalKeyPrefix + "cust-1"))).Should().BeNull();
    }
}
