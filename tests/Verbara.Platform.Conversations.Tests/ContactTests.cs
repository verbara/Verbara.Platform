using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Tests;

public class ContactTests
{
    [Fact]
    public void Constructor_ShouldCreateContact_WhenValidInput()
    {
        var contact = new Contact
        {
            ContactId = EntityId.From("c-001"),
            TenantId = new TenantId("t1"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        contact.ContactId.Value.Should().Be("c-001");
        contact.Addresses.Should().BeEmpty();
        contact.DoNotContact.Should().BeFalse();
    }

    [Fact]
    public void AddAddress_ShouldAddChannelAddress()
    {
        var contact = new Contact
        {
            ContactId = EntityId.From("c-001"),
            TenantId = new TenantId("t1"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "+1234567890"));

        contact.Addresses.Should().HaveCount(1);
        contact.Addresses[0].Channel.Should().Be(ChannelType.WhatsApp);
    }

    [Fact]
    public void AddAddress_ShouldNotDuplicate_WhenSameAddressExists()
    {
        var contact = new Contact
        {
            ContactId = EntityId.From("c-001"),
            TenantId = new TenantId("t1"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "+1234567890"));
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "+1234567890"));

        contact.Addresses.Should().HaveCount(1);
    }

    [Fact]
    public void FindAddress_ShouldReturnAddress_WhenExists()
    {
        var contact = new Contact
        {
            ContactId = EntityId.From("c-001"),
            TenantId = new TenantId("t1"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        contact.AddAddress(new ChannelAddress(ChannelType.Email, "test@example.com"));

        contact.FindAddress(ChannelType.Email).Should().NotBeNull();
        contact.FindAddress(ChannelType.Sms).Should().BeNull();
    }

    [Fact]
    public void RemoveAddress_ShouldRemoveExistingAddress()
    {
        var contact = new Contact
        {
            ContactId = EntityId.From("c-001"),
            TenantId = new TenantId("t1"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var address = new ChannelAddress(ChannelType.Email, "test@example.com");
        contact.AddAddress(address);
        contact.RemoveAddress(address);

        contact.Addresses.Should().BeEmpty();
    }

    [Fact]
    public void SetCustomField_ShouldStoreKeyValue()
    {
        var contact = new Contact
        {
            ContactId = EntityId.From("c-001"),
            TenantId = new TenantId("t1"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        contact.SetCustomField("vip", "true");

        contact.CustomFields.Should().ContainKey("vip");
        contact.CustomFields["vip"].Should().Be("true");
    }

    [Fact]
    public void SetConsent_ShouldTrackChannelConsent()
    {
        var contact = new Contact
        {
            ContactId = EntityId.From("c-001"),
            TenantId = new TenantId("t1"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        contact.SetConsent(ChannelType.Sms, true);
        contact.SetConsent(ChannelType.Email, false);

        contact.HasConsent(ChannelType.Sms).Should().BeTrue();
        contact.HasConsent(ChannelType.Email).Should().BeFalse();
        contact.HasConsent(ChannelType.WhatsApp).Should().BeFalse();
    }

    [Theory]
    [InlineData("John", "Doe", "John Doe")]
    [InlineData("John", null, "John")]
    [InlineData(null, "Doe", "Doe")]
    [InlineData(null, null, null)]
    public void FullName_ShouldCombineNames(string? first, string? last, string? expected)
    {
        var contact = new Contact
        {
            ContactId = EntityId.From("c-001"),
            TenantId = new TenantId("t1"),
            FirstName = first,
            LastName = last,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        contact.FullName.Should().Be(expected);
    }

    [Fact]
    public void FindAddress_ShouldSkipWhatsAppBsuid_WhenPhoneIsAlsoKnown()
    {
        var contact = new Contact { ContactId = EntityId.From("c-wa"), TenantId = new TenantId("t1"), CreatedAt = DateTimeOffset.UtcNow };
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "US.13491208655302741918"));
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "15550000002"));

        contact.FindAddress(ChannelType.WhatsApp)!.Address.Should().Be("15550000002");
    }

    [Fact]
    public void FindAddress_ShouldReturnNull_WhenWhatsAppContactHasOnlyBsuid()
    {
        var contact = new Contact { ContactId = EntityId.From("c-wa"), TenantId = new TenantId("t1"), CreatedAt = DateTimeOffset.UtcNow };
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, "US.13491208655302741918"));

        contact.FindAddress(ChannelType.WhatsApp).Should().BeNull("the Cloud API cannot send to a BSUID yet");
    }

    private static Contact WithAddresses(params (ChannelType Channel, string Address)[] addresses)
    {
        var contact = new Contact
        {
            ContactId = EntityId.From("c-001"),
            TenantId = new TenantId("t1"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        foreach (var (channel, address) in addresses)
            contact.AddAddress(new ChannelAddress(channel, address));
        return contact;
    }

    [Fact]
    public void FindAddress_ShouldSkipIdentityKeys_WhenContactHoldsThemFirst()
    {
        var contact = WithAddresses(
            (ChannelType.WebChat, "webchat-visitor:rcfqwP4uUDSA6hgRqgUIE3hCFFsXvDuxhE3MgUu0Y1M"),
            (ChannelType.WebChat, "webchat-external:cust-48151623"),
            (ChannelType.WebChat, "webchat-6a7b8c9d0e1f42a3b4c5d6e7f8a9b0c1"));

        contact.FindAddress(ChannelType.WebChat)!.Address.Should().Be("webchat-6a7b8c9d0e1f42a3b4c5d6e7f8a9b0c1");
    }

    [Fact]
    public void FindAddress_ShouldReturnNull_WhenContactHoldsOnlyIdentityKeys()
    {
        var contact = WithAddresses(
            (ChannelType.WebChat, "webchat-visitor:abc"),
            (ChannelType.WebChat, "webchat-external:cust-48151623"));

        contact.FindAddress(ChannelType.WebChat).Should().BeNull();
    }

    [Fact]
    public void FindAddress_ShouldReturnAddress_WhenPrefixDiffersInCase()
    {
        var contact = WithAddresses((ChannelType.WebChat, "WebChat-External:x"));

        contact.FindAddress(ChannelType.WebChat)!.Address.Should().Be("WebChat-External:x");
        contact.IdentityLevel.Should().Be(ContactIdentityLevel.Anonymous);
    }

    [Fact]
    public void FindAddress_ShouldReturnAddress_WhenReservedPrefixIsOnAnotherChannel()
    {
        var contact = WithAddresses((ChannelType.Sms, "webchat-external:x"));

        contact.FindAddress(ChannelType.Sms)!.Address.Should().Be("webchat-external:x");
        contact.IdentityLevel.Should().BeNull();
    }

    [Fact]
    public void IdentityLevel_ShouldPreferVerified_WhenContactHoldsBothKeys()
    {
        var contact = WithAddresses(
            (ChannelType.WebChat, "webchat-visitor:abc"),
            (ChannelType.WebChat, "webchat-external:x"));

        contact.IdentityLevel.Should().Be(ContactIdentityLevel.Verified);
    }

    [Fact]
    public void IdentityLevel_ShouldFollowAddresses_WhenAddressIsRemoved()
    {
        var contact = WithAddresses(
            (ChannelType.WebChat, "webchat-external:x"),
            (ChannelType.WebChat, "webchat-6a7b8c9d0e1f42a3b4c5d6e7f8a9b0c1"));

        contact.RemoveAddress(new ChannelAddress(ChannelType.WebChat, "webchat-external:x"));

        contact.IdentityLevel.Should().Be(ContactIdentityLevel.Anonymous, "the level is computed, never stored");
    }
}
