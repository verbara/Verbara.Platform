using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Xunit;

namespace Verbara.Platform.Llm.Tests;

public sealed class DefaultLlmProviderResolverTests
{
    private static readonly EntityId Tenant = EntityId.From("tenant-1");

    private static TenantLlmConfig Config(
        ProviderType type = ProviderType.OpenAiCompatible,
        string model = "gpt-test",
        bool enabled = true,
        ProviderSettings? settings = null,
        string? apiKey = "sk-live-1234") => new()
    {
        TenantId = Tenant,
        ProviderType = type,
        Model = model,
        ApiKey = apiKey,
        ApiKeyLast4 = apiKey?.Length >= 4 ? apiKey[^4..] : apiKey,
        Settings = settings ?? new ProviderSettings(),
        Enabled = enabled,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static DefaultLlmProviderResolver CreateResolver(ITenantLlmConfigStore store) =>
        new(store, new StubHttpClientFactory(), new ServiceCollection().BuildServiceProvider());

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenNoConfig()
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns((TenantLlmConfig?)null);
        var resolver = CreateResolver(store);

        var result = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenDisabled()
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(Config(enabled: false));
        var resolver = CreateResolver(store);

        var result = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldBuildOpenAiProvider_WhenProviderTypeOpenAiCompatible()
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(
            Config(ProviderType.OpenAiCompatible, settings: new ProviderSettings { BaseUrl = "https://x/v1" }));
        var resolver = CreateResolver(store);

        var result = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Provider.Should().BeOfType<OpenAiCompatibleLlmProvider>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldBuildAzureProvider_WhenProviderTypeAzureOpenAi()
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(
            Config(ProviderType.AzureOpenAi, settings: new ProviderSettings
            {
                BaseUrl = "https://r.openai.azure.com",
                AzureDeployment = "dep",
                AzureApiVersion = "2024-06-01",
            }));
        var resolver = CreateResolver(store);

        var result = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Provider.Should().BeOfType<AzureOpenAiLlmProvider>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldBuildAnthropicProvider_WhenProviderTypeAnthropic()
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(
            Config(ProviderType.Anthropic, settings: new ProviderSettings { AnthropicVersion = "2024-10-22" }));
        var resolver = CreateResolver(store);

        var result = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Provider.Should().BeOfType<AnthropicLlmProvider>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldCarryModelId_FromConfig()
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(Config(model: "gpt-4o-mini"));
        var resolver = CreateResolver(store);

        var result = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ModelId.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnCached_WhenConfigUnchanged()
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(Config());
        var resolver = CreateResolver(store);

        var first = await resolver.ResolveAsync(Tenant, CancellationToken.None);
        var second = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        // Same fingerprint ⇒ the exact same built provider instance is reused.
        second!.Provider.Should().BeSameAs(first!.Provider);
    }

    [Fact]
    public async Task ResolveAsync_ShouldRebuild_WhenConfigChanged()
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(Config(model: "gpt-a"), Config(model: "gpt-b"));
        var resolver = CreateResolver(store);

        var first = await resolver.ResolveAsync(Tenant, CancellationToken.None);
        var second = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        // Different fingerprint (model changed) ⇒ a freshly built provider.
        second!.Provider.Should().NotBeSameAs(first!.Provider);
        second.ModelId.Should().Be("gpt-b");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRebuild_WhenInvalidated()
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(Config());
        var resolver = CreateResolver(store);

        var first = await resolver.ResolveAsync(Tenant, CancellationToken.None);
        resolver.Invalidate(Tenant);
        var second = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        // Cache evicted ⇒ rebuilt even though the config is identical.
        second!.Provider.Should().NotBeSameAs(first!.Provider);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHttpMessageHandler(System.Net.HttpStatusCode.OK, "{}"));
    }
}
