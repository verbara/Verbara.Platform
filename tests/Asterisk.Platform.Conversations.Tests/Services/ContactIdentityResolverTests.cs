using Asterisk.Platform.Conversations.Services;
using Asterisk.Platform.Core;
using NSubstitute;

namespace Asterisk.Platform.Conversations.Tests.Services;

public class ContactIdentityResolverTests
{
    private readonly IContactStore _contactStore = Substitute.For<IContactStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly DefaultContactIdentityResolver _sut;

    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly ChannelAddress Address = new(ChannelType.WhatsApp, "+1234567890");
    private static readonly DateTimeOffset Now = new(2026, 3, 21, 12, 0, 0, TimeSpan.Zero);

    public ContactIdentityResolverTests()
    {
        _clock.UtcNow.Returns(Now);
        _sut = new DefaultContactIdentityResolver(_contactStore, _clock);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnExistingContact_WhenFound()
    {
        var existing = new Contact
        {
            ContactId = EntityId.From("c-existing"),
            TenantId = Tenant,
            CreatedAt = Now,
        };
        _contactStore.FindByAddressAsync(Tenant, Address, Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _sut.ResolveAsync(Tenant, Address, CancellationToken.None);

        result.Should().BeSameAs(existing);
        await _contactStore.DidNotReceive().SaveAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ShouldCreateNewContact_WhenNotFound()
    {
        _contactStore.FindByAddressAsync(Tenant, Address, Arg.Any<CancellationToken>())
            .Returns((Contact?)null);

        var result = await _sut.ResolveAsync(Tenant, Address, CancellationToken.None);

        result.Should().NotBeNull();
        await _contactStore.Received(1).SaveAsync(result, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ShouldAddAddress_ToNewContact()
    {
        _contactStore.FindByAddressAsync(Tenant, Address, Arg.Any<CancellationToken>())
            .Returns((Contact?)null);

        var result = await _sut.ResolveAsync(Tenant, Address, CancellationToken.None);

        result.Addresses.Should().ContainSingle(a => a.Channel == Address.Channel && a.Address == Address.Address);
    }

    [Fact]
    public async Task ResolveAsync_ShouldSetTenantId_OnNewContact()
    {
        _contactStore.FindByAddressAsync(Tenant, Address, Arg.Any<CancellationToken>())
            .Returns((Contact?)null);

        var result = await _sut.ResolveAsync(Tenant, Address, CancellationToken.None);

        result.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task ResolveAsync_ShouldSetCreatedAt_FromClock()
    {
        _contactStore.FindByAddressAsync(Tenant, Address, Arg.Any<CancellationToken>())
            .Returns((Contact?)null);

        var result = await _sut.ResolveAsync(Tenant, Address, CancellationToken.None);

        result.CreatedAt.Should().Be(Now);
    }
}
