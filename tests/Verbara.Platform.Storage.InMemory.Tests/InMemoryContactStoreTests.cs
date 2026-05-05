using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryContactStoreTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static Contact MakeContact(TenantId tenantId) =>
        new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnContact_WhenAddressMatches()
    {
        var store = new InMemoryContactStore();
        var contact = MakeContact(Tenant);
        var address = new ChannelAddress(ChannelType.WhatsApp, "+15551234567");
        contact.AddAddress(address);
        await store.SaveAsync(contact, CancellationToken.None);

        var result = await store.FindByAddressAsync(Tenant, address, CancellationToken.None);

        result.Should().BeSameAs(contact);
    }

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnNull_WhenNoMatch()
    {
        var store = new InMemoryContactStore();

        var result = await store.FindByAddressAsync(Tenant, new ChannelAddress(ChannelType.Sms, "+10000000000"), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveContact()
    {
        var store = new InMemoryContactStore();
        var contact = MakeContact(Tenant);
        await store.SaveAsync(contact, CancellationToken.None);

        await store.DeleteAsync(Tenant, contact.ContactId, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant, contact.ContactId, CancellationToken.None);
        result.Should().BeNull();
    }
}
