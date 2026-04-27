using Asterisk.Platform.Core.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Asterisk.Platform.Core.Tests.DependencyInjection;

public sealed class HostedServicePromotionExtensionsTests
{
    private sealed class FakeHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void PromoteHostedServiceToSingleton_ShouldResolveAsBothInterfaces_ToSameInstance()
    {
        var services = new ServiceCollection();
        services.AddHostedService<FakeHostedService>();

        services.PromoteHostedServiceToSingleton<FakeHostedService>();

        using var sp = services.BuildServiceProvider();
        var asConcrete = sp.GetRequiredService<FakeHostedService>();
        var asInterface = sp.GetServices<IHostedService>().OfType<FakeHostedService>().Single();

        asConcrete.Should().BeSameAs(asInterface);
    }

    [Fact]
    public void PromoteHostedServiceToSingleton_ShouldBeIdempotent_WhenCalledTwice()
    {
        var services = new ServiceCollection();
        services.AddHostedService<FakeHostedService>();

        services.PromoteHostedServiceToSingleton<FakeHostedService>();
        services.PromoteHostedServiceToSingleton<FakeHostedService>();

        using var sp = services.BuildServiceProvider();
        sp.GetServices<IHostedService>().OfType<FakeHostedService>().Should().HaveCount(1);
    }

    [Fact]
    public void PromoteHostedServiceToSingleton_ShouldWork_WhenAddHostedServiceNotCalledFirst()
    {
        // Defensive: even if AddHostedService<T> not called previously, promotion still wires both registrations.
        var services = new ServiceCollection();

        services.PromoteHostedServiceToSingleton<FakeHostedService>();

        using var sp = services.BuildServiceProvider();
        var asConcrete = sp.GetRequiredService<FakeHostedService>();
        var asInterface = sp.GetServices<IHostedService>().OfType<FakeHostedService>().Single();
        asConcrete.Should().BeSameAs(asInterface);
    }
}
