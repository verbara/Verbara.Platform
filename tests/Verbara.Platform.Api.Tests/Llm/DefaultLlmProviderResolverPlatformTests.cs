using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Api.Tests.Llm;

public sealed class DefaultLlmProviderResolverPlatformTests
{
    private static (DefaultLlmProviderResolver resolver, ITenantLlmConfigStore store) Build(PlatformLlmOptions platform)
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        // Real empty provider: supports keyed services (the ctor probes GetKeyedService<ResiliencePolicy>)
        // and returns null for the unregistered policy — which the resolver tolerates (NoOp fallback).
        var sp = new ServiceCollection().BuildServiceProvider();
        var resolver = new DefaultLlmProviderResolver(store, httpFactory, sp,
            meterFactory: null, loggerFactory: null, platformOptions: Options.Create(platform));
        return (resolver, store);
    }

    private static TenantLlmConfig PlatformCfg() => new()
    {
        TenantId = EntityId.From("t1"),
        ProviderType = ProviderType.OpenAiCompatible,
        Model = "ignored-when-platform",
        AiSource = AiSource.PlatformManaged,
        Enabled = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task ResolveAsync_ShouldReturnProvider_WhenPlatformManagedAndOperatorEnabled()
    {
        var (resolver, store) = Build(new PlatformLlmOptions { Enabled = true, ApiKey = "op-key", Model = "gpt-x", BaseUrl = "https://op" });
        store.GetAsync(Arg.Any<EntityId>(), Arg.Any<CancellationToken>()).Returns(PlatformCfg());

        var resolved = await resolver.ResolveAsync(EntityId.From("t1"), CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ModelId.Should().Be("gpt-x"); // platform model wins, not config.Model
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenPlatformManagedButOperatorDisabled()
    {
        var (resolver, store) = Build(new PlatformLlmOptions { Enabled = false, ApiKey = "op-key", Model = "gpt-x" });
        store.GetAsync(Arg.Any<EntityId>(), Arg.Any<CancellationToken>()).Returns(PlatformCfg());

        var resolved = await resolver.ResolveAsync(EntityId.From("t1"), CancellationToken.None);

        resolved.Should().BeNull(); // fail-closed: operator disabled platform LLM
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotRequireTenantKey_WhenPlatformManaged()
    {
        var (resolver, store) = Build(new PlatformLlmOptions { Enabled = true, ApiKey = "op-key", Model = "gpt-x" });
        store.GetAsync(Arg.Any<EntityId>(), Arg.Any<CancellationToken>()).Returns(PlatformCfg()); // config has NO ApiKey

        var resolved = await resolver.ResolveAsync(EntityId.From("t1"), CancellationToken.None);

        resolved.Should().NotBeNull(); // BYO key-guard is bypassed for PlatformManaged
    }
}
