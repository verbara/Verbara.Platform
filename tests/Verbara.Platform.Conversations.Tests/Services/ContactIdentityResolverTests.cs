using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace Verbara.Platform.Conversations.Tests.Services;

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

    // ── whatsapp-accepts-business-scoped-user-ids ──────────────────────────────

    private static readonly ChannelAddress Bsuid = new(ChannelType.WhatsApp, "US.13491208655302741918");
    private static readonly ChannelAddress Phone = new(ChannelType.WhatsApp, "15550000002");

    private static Contact ContactWith(params ChannelAddress[] addresses)
    {
        var contact = new Contact { ContactId = EntityId.From("c-wa"), TenantId = Tenant, CreatedAt = Now };
        foreach (var a in addresses)
            contact.AddAddress(a);
        return contact;
    }

    [Fact]
    public async Task ResolveAsync_ShouldPreferBsuid_WhenBothAddressesMatchDifferentContacts()
    {
        var byBsuid = ContactWith(Bsuid, Phone);
        _contactStore.FindByAddressAsync(Tenant, Bsuid, Arg.Any<CancellationToken>()).Returns(byBsuid);

        var result = await _sut.ResolveAsync(Tenant, new ContactIdentity(Bsuid, [Phone], null), CancellationToken.None);

        result.Should().BeSameAs(byBsuid);
        await _contactStore.DidNotReceive().FindByAddressAsync(Tenant, Phone, Arg.Any<CancellationToken>());
        await _contactStore.DidNotReceive().SaveAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ShouldWriteBsuidBackToPhoneOnlyContact_WhenBothArrive()
    {
        var phoneOnly = ContactWith(Phone);
        _contactStore.FindByAddressAsync(Tenant, Bsuid, Arg.Any<CancellationToken>()).ReturnsNull();
        _contactStore.FindByAddressAsync(Tenant, Phone, Arg.Any<CancellationToken>()).Returns(phoneOnly);

        var result = await _sut.ResolveAsync(Tenant, new ContactIdentity(Bsuid, [Phone], null), CancellationToken.None);

        result.Should().BeSameAs(phoneOnly);
        result.Addresses.Should().BeEquivalentTo([Phone, Bsuid]);
        result.UpdatedAt.Should().Be(Now);
        await _contactStore.Received(1).SaveAsync(phoneOnly, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ShouldAddPhoneToBsuidOnlyContact_WhenPhoneArrivesLater()
    {
        var bsuidOnly = ContactWith(Bsuid);
        _contactStore.FindByAddressAsync(Tenant, Bsuid, Arg.Any<CancellationToken>()).Returns(bsuidOnly);

        var result = await _sut.ResolveAsync(Tenant, new ContactIdentity(Bsuid, [Phone], null), CancellationToken.None);

        result.Addresses.Should().BeEquivalentTo([Bsuid, Phone]);
        result.FindAddress(ChannelType.WhatsApp).Should().Be(Phone, "outbound stays on the phone");
        await _contactStore.Received(1).SaveAsync(bsuidOnly, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ShouldCreateOneContactWithBothAddressesAndName_WhenNoneMatches()
    {
        _contactStore.FindByAddressAsync(Tenant, Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        var result = await _sut.ResolveAsync(Tenant, new ContactIdentity(Bsuid, [Phone], "Sheena"), CancellationToken.None);

        result.Addresses.Should().BeEquivalentTo([Bsuid, Phone]);
        result.FirstName.Should().Be("Sheena");
        result.UpdatedAt.Should().BeNull();
        await _contactStore.Received(1).SaveAsync(result, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ShouldCopyProfileName_WhenExistingContactHasNoName()
    {
        var unnamed = ContactWith(Phone);
        _contactStore.FindByAddressAsync(Tenant, Phone, Arg.Any<CancellationToken>()).Returns(unnamed);

        var result = await _sut.ResolveAsync(Tenant, new ContactIdentity(Phone, [], "  Alice  "), CancellationToken.None);

        result.FirstName.Should().Be("Alice");
        await _contactStore.Received(1).SaveAsync(unnamed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ShouldKeepExistingName_WhenContactAlreadyHasOne()
    {
        var named = ContactWith(Phone);
        named.LastName = "Gómez";
        _contactStore.FindByAddressAsync(Tenant, Phone, Arg.Any<CancellationToken>()).Returns(named);

        var result = await _sut.ResolveAsync(Tenant, new ContactIdentity(Phone, [], "Alice"), CancellationToken.None);

        result.FirstName.Should().BeNull("a name an agent or import set is never overwritten");
        await _contactStore.DidNotReceive().SaveAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>());
    }
}
