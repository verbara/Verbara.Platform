using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Core.Tests;

public class TenantChannelConfigTests
{
    [Fact]
    public void TenantChannelConfig_ShouldCreateWithCredentials_WhenAllPropertiesProvided()
    {
        var tenantId = new TenantId("tenant-1");
        var credentials = new Dictionary<string, string>
        {
            ["AccessToken"] = "token-abc",
            ["PhoneNumberId"] = "1234567890",
        };

        var config = new TenantChannelConfig
        {
            TenantId = tenantId,
            Channel = ChannelType.WhatsApp,
            Credentials = credentials,
        };

        config.TenantId.Should().Be(tenantId);
        config.Channel.Should().Be(ChannelType.WhatsApp);
        config.Credentials.Should().HaveCount(2);
        config.Credentials["AccessToken"].Should().Be("token-abc");
        config.IsActive.Should().BeTrue();
    }

    [Fact]
    public void TenantChannelConfig_ShouldDefaultToActive_WhenCreated()
    {
        var config = new TenantChannelConfig
        {
            TenantId = new TenantId("tenant-2"),
            Channel = ChannelType.Sms,
            Credentials = new Dictionary<string, string>(),
        };

        config.IsActive.Should().BeTrue();
    }

    [Fact]
    public void TenantChannelConfig_ShouldAllowDeactivation_WhenIsActiveSetToFalse()
    {
        var config = new TenantChannelConfig
        {
            TenantId = new TenantId("tenant-3"),
            Channel = ChannelType.WebChat,
            Credentials = new Dictionary<string, string>(),
        };

        config.IsActive = false;

        config.IsActive.Should().BeFalse();
    }

    [Fact]
    public void TenantChannelConfig_ShouldImplementITenantScoped()
    {
        var tenantId = new TenantId("tenant-4");
        TenantChannelConfig config = new()
        {
            TenantId = tenantId,
            Channel = ChannelType.WhatsApp,
            Credentials = new Dictionary<string, string>(),
        };

        ((ITenantScoped)config).TenantId.Should().Be(tenantId);
    }
}
