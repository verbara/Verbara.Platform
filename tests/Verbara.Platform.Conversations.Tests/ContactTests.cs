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
}
