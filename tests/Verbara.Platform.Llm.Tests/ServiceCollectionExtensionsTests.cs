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
    public void AddPlatformLlm_ShouldNotRegisterGlobalProvider_WhenNotConfigured()
    {
        // Flows depends on ILlmProvider only being the OpenAI typed-client when a global key
        // is configured; unconfigured leaves the DisabledLlmProvider TryAdd (from AddPlatformFlows)
        // to win. P2c.1: the resolver + keyed policy + named clients are still registered (always).
        var services = new ServiceCollection();

        services.AddPlatformLlm(o =>
        {
            // Only a partial config — not enough to be IsConfigured.
            o.BaseUrl = "https://api.example.test/v1";
        });

        using var provider = services.BuildServiceProvider();
        provider.GetService<ILlmProvider>().Should().BeNull(
            "no global key ⇒ the global typed-client is not registered");
    }

    [Fact]
    public void AddPlatformLlm_ShouldAlwaysRegisterResolver_EvenWhenNotConfigured()
    {
        var services = new ServiceCollection();

        // The resolver depends on ITenantLlmConfigStore (registered by the storage layer in the
        // real host); supply a stub so activation succeeds in this isolated container.
        services.AddSingleton(NSubstitute.Substitute.For<ITenantLlmConfigStore>());

        services.AddPlatformLlm(o =>
        {
            // Deliberately unconfigured.
            o.BaseUrl = "https://api.example.test/v1";
        });

        using var provider = services.BuildServiceProvider();
        provider.GetService<ILlmProviderResolver>().Should()
            .BeOfType<DefaultLlmProviderResolver>(
                "per-tenant BYO resolution must work even when no global key is set");
    }
}
