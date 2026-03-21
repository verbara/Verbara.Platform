using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Queues.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddPlatformQueues_ShouldRegisterOptions()
    {
        var services = new ServiceCollection();
        services.AddPlatformQueues();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<QueueOptions>>();
        options.Value.Should().NotBeNull();
    }
}
