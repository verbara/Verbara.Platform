using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Identity.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddPlatformIdentity_ShouldRegisterOptions()
    {
        var services = new ServiceCollection();
        services.AddPlatformIdentity();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<IdentityOptions>>();
        options.Value.Should().NotBeNull();
    }

    [Fact]
    public void AddPlatformIdentity_ShouldApplyConfiguration_WhenProvided()
    {
        var services = new ServiceCollection();
        services.AddPlatformIdentity(o => o.DefaultApiKeyRateLimitPerMinute = 100);
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<IdentityOptions>>();
        options.Value.DefaultApiKeyRateLimitPerMinute.Should().Be(100);
    }
}
