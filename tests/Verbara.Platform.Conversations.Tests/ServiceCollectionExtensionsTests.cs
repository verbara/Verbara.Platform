using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Conversations.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddPlatformConversations_ShouldRegisterOptions()
    {
        var services = new ServiceCollection();
        services.AddPlatformConversations();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ConversationOptions>>();
        options.Value.Should().NotBeNull();
    }
}
