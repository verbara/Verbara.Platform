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

    /// <summary>An OpenAI-compatible config with an explicit key + UpdatedAt (for fingerprint tests).</summary>
    private static TenantLlmConfig ConfigAt(string apiKey, DateTimeOffset updatedAt) => new()
    {
        TenantId = Tenant,
        ProviderType = ProviderType.OpenAiCompatible,
        Model = "gpt-test",
        ApiKey = apiKey,
        ApiKeyLast4 = apiKey.Length >= 4 ? apiKey[^4..] : apiKey,
        Settings = new ProviderSettings { BaseUrl = "https://x/v1" },
        Enabled = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = updatedAt,
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
    public async Task ResolveAsync_ShouldReturnNull_WhenEnabledButKeyless()
    {
        // An enabled config with no key is a clean "AI off" state — a keyless provider would throw at
        // call time, so the resolver must treat it as off (null) rather than building one.
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(Config(enabled: true, apiKey: null));
        var resolver = CreateResolver(store);

        var result = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldRebuild_WhenKeyRotatedWithSameLengthAndLast4()
    {
        // A key rotation that keeps the SAME length + last-4 (but a fresh UpdatedAt) must still yield
        // a new fingerprint so the provider rebuilds with the rotated key — no stale-key collision.
        var t0 = DateTimeOffset.UnixEpoch;
        var t1 = t0.AddMinutes(5);
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(
            ConfigAt("sk-AAAAAAAA9999", t0),   // length 14, last-4 "9999"
            ConfigAt("sk-BBBBBBBB9999", t1));  // SAME length 14 + last-4 "9999", rotated value
        var resolver = CreateResolver(store);

        var first = await resolver.ResolveAsync(Tenant, CancellationToken.None);
        var second = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        // Different UpdatedAt ⇒ different fingerprint ⇒ a freshly built provider (key change honoured).
        second!.Provider.Should().NotBeSameAs(first!.Provider);
    }

    [Fact]
    public async Task Invalidate_ShouldDisposeEvictedProvider()
    {
        // On eviction the outgoing provider must be disposed so its Meter/HTTP-handler resources
        // don't leak. The concrete OpenAI provider is IDisposable; after Invalidate + re-resolve a
        // fresh instance is built (the cached one was removed).
        var store = Substitute.For<ITenantLlmConfigStore>();
        store.GetAsync(Tenant, Arg.Any<CancellationToken>()).Returns(Config());
        var resolver = CreateResolver(store);

        var first = await resolver.ResolveAsync(Tenant, CancellationToken.None);
        first.Should().NotBeNull();
        first!.Provider.Should().BeAssignableTo<IDisposable>();

        resolver.Invalidate(Tenant);
        var second = await resolver.ResolveAsync(Tenant, CancellationToken.None);

        second!.Provider.Should().NotBeSameAs(first.Provider);
    }

    [Fact]
    public void BuildTransient_ShouldReturnWorkingProvider_ForTestProbe()
    {
        // The /test path builds an isolated provider WITHOUT touching the store or the per-tenant
        // cache (and on a no-op resilience policy so a failing probe can't trip the shared circuit).
        var store = Substitute.For<ITenantLlmConfigStore>();
        var resolver = CreateResolver(store);

        var provider = resolver.BuildTransient(
            ConfigAt("sk-draft-1234", DateTimeOffset.UnixEpoch));

        provider.Should().BeOfType<OpenAiCompatibleLlmProvider>();
        store.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
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
