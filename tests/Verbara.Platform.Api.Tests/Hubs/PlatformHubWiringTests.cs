using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Hubs;
using Verbara.Sdk.Pro.Push.SignalR.Diagnostics;
using Verbara.Sdk.Pro.Push.SignalR.Hubs;
using Verbara.Sdk.Pro.Push.SignalR.Presence;
using Verbara.Sdk.Push.Authz;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Hubs;

/// <summary>
/// Smoke tests that verify the Plan 32C Platform wiring actually resolves
/// through the production DI graph: RbacSubscriptionAuthorizer replaces the
/// SDK AllowAll default, PlatformSupervisorCoordinator replaces the Pro
/// NotImplemented default, and the hub tier (tracker + metrics) is available.
///
/// Full connection-level hub tests (JWT upgrade, policy enforcement,
/// OnConnectedAsync tracking, method delegation) are out of scope for unit
/// CI — they would require a real WebSocket handshake against a hosted
/// WebApplicationFactory and belong in a separate functional-test project.
/// </summary>
public sealed class PlatformHubWiringTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private readonly UnifiedPlatformApiFactory _factory;

    public PlatformHubWiringTests(UnifiedPlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Services_ShouldResolveRbacSubscriptionAuthorizer_WhenPushSignalRRegistered()
    {
        using var scope = _factory.Services.CreateScope();

        var authorizer = scope.ServiceProvider.GetRequiredService<ISubscriptionAuthorizer>();

        authorizer.Should().BeOfType<RbacSubscriptionAuthorizer>();
    }

    [Fact]
    public void Services_ShouldResolvePlatformSupervisorCoordinator_WhenPushSignalRRegistered()
    {
        using var scope = _factory.Services.CreateScope();

        var coordinator = scope.ServiceProvider.GetRequiredService<ISupervisorCoordinator>();

        coordinator.Should().BeOfType<PlatformSupervisorCoordinator>();
    }

    [Fact]
    public void Services_ShouldResolvePresenceTrackerAsSingleton_WhenPushSignalRRegistered()
    {
        var a = _factory.Services.GetRequiredService<PresenceTracker>();
        var b = _factory.Services.GetRequiredService<PresenceTracker>();

        a.Should().BeSameAs(b);
    }

    [Fact]
    public void Services_ShouldResolvePlatformHubMetrics_WhenPushSignalRRegistered()
    {
        var metrics = _factory.Services.GetRequiredService<PlatformHubMetrics>();

        metrics.ConnectionsOpened.Should().NotBeNull();
        metrics.MethodInvoked.Should().NotBeNull();
    }
}
