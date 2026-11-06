using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Api.Tests.Routing;

/// <summary>
/// conversations-return-to-last-agent task 2.1 (design D4, ruling Q12): the tenant's effective inactivity window is
/// resolved once, by <see cref="TenantInactivityWindowResolver"/>, for the inactivity close and for last-agent routing's
/// clamp, with the inactivity close's validity rules (minimum 1 h, the host window on an invalid override).
/// </summary>
public sealed class TenantInactivityWindowResolverTests
{
    private const string TenantA = "t-window";

    private static async Task<InMemoryTenantAuthConfigStore> StoreWith(int? hours)
    {
        var store = new InMemoryTenantAuthConfigStore();
        await store.SaveAsync(new TenantAuthConfig { TenantId = TenantA }, CancellationToken.None);
        store.SeedInactivityCloseAfterHours(TenantA, hours);
        return store;
    }

    private static TenantInactivityWindowResolver Resolver(ITenantAuthConfigStore? store, int hostHours = 72) =>
        new(new ConversationOptions { InactivityCloseAfterHours = hostHours }, store);

    [Theory]
    [InlineData(120, 120, true)]  // a valid override above the host
    [InlineData(24, 24, true)]    // below WhatsApp's window still applies (the worker warns)
    [InlineData(1, 1, true)]      // the minimum
    [InlineData(0, 72, false)]    // below the minimum: the host window
    [InlineData(-5, 72, false)]
    public async Task ResolveAsync_ShouldApplyTheValidityRules_WhenTheTenantStoresAnOverride(
        int stored, int expectedHours, bool expectedApplied)
    {
        var resolution = await Resolver(await StoreWith(stored)).ResolveAsync(new TenantId(TenantA), CancellationToken.None);

        Assert.Equal(new InactivityWindowResolution(expectedHours, stored, expectedApplied), resolution);
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseTheHostWindow_WhenTheColumnIsNullTheRowIsMissingOrThereIsNoStore()
    {
        var host = new InactivityWindowResolution(96, null, OverrideApplied: false);

        Assert.Equal(host, await Resolver(await StoreWith(null), 96).ResolveAsync(new TenantId(TenantA), CancellationToken.None));
        Assert.Equal(host, await Resolver(new InMemoryTenantAuthConfigStore(), 96).ResolveAsync(new TenantId(TenantA), CancellationToken.None));
        Assert.Equal(host, await Resolver(null, 96).ResolveAsync(new TenantId(TenantA), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenTheTenantConfigCannotBeRead()
    {
        var store = Substitute.For<ITenantAuthConfigStore>();
        store.GetAsync(TenantA, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Resolver(store).ResolveAsync(new TenantId(TenantA), CancellationToken.None));
    }

    [Fact]
    public void Host_ShouldRegisterTheResolverRoutingReads_WhenTheInactivityCloseIsAdded()
    {
        using var factory = new PlatformApiFactory();

        var resolver = factory.Services.GetService(typeof(IInactivityWindowResolver));

        Assert.IsType<TenantInactivityWindowResolver>(resolver);
        // The host builds the routing chain, LastAgentMiddleware and its new dependencies included.
        var chain = (IReadOnlyList<Verbara.Platform.Routing.Inbound.InboundRoutingMiddlewareBase>)factory.Services
            .GetService(typeof(IReadOnlyList<Verbara.Platform.Routing.Inbound.InboundRoutingMiddlewareBase>))!;
        Assert.Contains(chain, m => m is Verbara.Platform.Routing.Inbound.Middlewares.LastAgentMiddleware);
    }
}
