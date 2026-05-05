using Verbara.Platform.Channels.Rcs;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Channels.Rcs.Tests;

public class RcsDiTests
{
    [Fact]
    public void AddRcs_ShouldRegisterConnectorWebhookHandlerAndTransformer()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRcsProvider>());
        services.AddRcs(o =>
        {
            o.AgentId = "my-agent";
            o.ServiceAccountJson = "{}";
        });

        var provider = services.BuildServiceProvider();

        provider.GetService<RcsConnector>().Should().NotBeNull();
        provider.GetService<RcsWebhookHandler>().Should().NotBeNull();
        provider.GetService<RcsMessageTransformer>().Should().NotBeNull();
    }

    [Fact]
    public void AddRcs_ShouldConfigureOptions_WithProvidedValues()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRcsProvider>());
        services.AddRcs(o =>
        {
            o.AgentId = "agent-xyz";
            o.ServiceAccountJson = "{\"type\":\"service_account\"}";
            o.BaseUrl = "https://custom.rbm.example.com";
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RcsOptions>>().Value;

        options.AgentId.Should().Be("agent-xyz");
        options.BaseUrl.Should().Be("https://custom.rbm.example.com");
    }
}
