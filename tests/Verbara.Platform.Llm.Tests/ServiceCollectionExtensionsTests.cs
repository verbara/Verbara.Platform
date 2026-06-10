using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Llm;
using Xunit;

namespace Verbara.Platform.Llm.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddPlatformLlm_ShouldRegisterRealProvider_WhenConfigured()
    {
        var services = new ServiceCollection();

        services.AddPlatformLlm(o =>
        {
            o.BaseUrl = "https://api.example.test/v1";
            o.ApiKey = "sk-test-key";
            o.Model = "gpt-test";
        });

        using var provider = services.BuildServiceProvider();
        var llm = provider.GetRequiredService<ILlmProvider>();

        llm.Should().BeOfType<OpenAiCompatibleLlmProvider>();
    }

    [Fact]
    public void AddPlatformLlm_ShouldLeaveContainerUnchanged_WhenNotConfigured()
    {
        var services = new ServiceCollection();

        services.AddPlatformLlm(o =>
        {
            // Only a partial config — not enough to be IsConfigured.
            o.BaseUrl = "https://api.example.test/v1";
        });

        services.Should().BeEmpty();

        using var provider = services.BuildServiceProvider();
        provider.GetService<ILlmProvider>().Should().BeNull();
    }
}
