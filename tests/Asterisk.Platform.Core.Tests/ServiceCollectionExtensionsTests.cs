using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Core.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddPlatformCore_ShouldRegisterClock()
    {
        var services = new ServiceCollection();
        services.AddPlatformCore();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IClock>().Should().BeOfType<SystemClock>();
    }
}
