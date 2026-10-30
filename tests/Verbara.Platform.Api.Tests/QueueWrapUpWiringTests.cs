using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Queues;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// What the built host hands the session engine for a queue's wrap-up. The Sdk's own default is also 30 s, so the
/// value alone cannot tell a host that set it from one that left it: one test runs the host's configuration over a
/// sentinel instead.
/// </summary>
public sealed class QueueWrapUpWiringTests(AuthenticatedPlatformApiFactory factory)
    : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly TimeSpan Fallback = TimeSpan.FromSeconds(30);

    [Fact]
    public void IWrapUpDurationProvider_ShouldResolveToTheQueueProviderAsASingleton_WhenTheHostIsBuilt()
    {
        var first = factory.Services.GetService<IWrapUpDurationProvider>();
        var second = factory.Services.GetService<IWrapUpDurationProvider>();

        first.Should().BeOfType<QueueWrapUpDurationProvider>();
        second.Should().BeSameAs(first);
        first.Should().BeSameAs(
            factory.Services.GetRequiredService<QueueWrapUpDurationProvider>(),
            "the engine must read the instance the save hook and the refresh write");
    }

    [Fact]
    public void SessionOptions_ShouldCarryThePlatformFallback_WhenTheHostIsBuilt()
    {
        WrapUpConfig.FallbackSeconds.Should().Be(30);

        factory.Services.GetRequiredService<IOptions<SessionOptions>>().Value.WrapUpDuration.Should().Be(Fallback);
    }

    [Fact]
    public void SessionOptions_ShouldBeSetByTheHost_WhenTheSdkDefaultIsTheSameValue()
    {
        var sentinel = new SessionOptions { WrapUpDuration = TimeSpan.FromSeconds(1) };

        foreach (var configure in factory.Services.GetServices<IConfigureOptions<SessionOptions>>())
        {
            configure.Configure(sentinel);
        }

        sentinel.WrapUpDuration.Should().Be(Fallback, "the host, not the Sdk's default, sets the fallback");
    }

    [Fact]
    public void SessionOptions_ShouldPassStartupValidation_WhenTheHostIsBuilt()
    {
        var validate = () => factory.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().NotThrow();
    }
}
