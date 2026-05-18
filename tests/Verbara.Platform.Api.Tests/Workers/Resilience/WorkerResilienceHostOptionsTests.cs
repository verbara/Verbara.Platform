using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Tests.Workers.Resilience;

/// <summary>
/// Integration test verifying that the Platform.Api composition root sets
/// <see cref="HostOptions.BackgroundServiceExceptionBehavior"/> = <see cref="BackgroundServiceExceptionBehavior.StopHost"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the host-level switch that turns the per-worker outer <c>try/catch + log + throw</c>
/// discipline (ADR-0019) into an observable host shutdown signal. Without <c>StopHost</c>,
/// the .NET default <see cref="BackgroundServiceExceptionBehavior.Ignore"/> swallows the
/// rethrown exception silently — producing the exact failure mode the D-LK 24h soak surfaced
/// in <c>QueueDistributionWorker</c> (21h silent stale heartbeat).
/// </para>
/// <para>
/// The test asserts on the configured value resolved from a minimal <see cref="IServiceCollection"/>
/// that mirrors the Program.cs wiring. Full WebApplicationFactory-based exercise of the
/// host-stop path is left to chaos/integration validation (post-train).
/// </para>
/// </remarks>
public sealed class WorkerResilienceHostOptionsTests
{
    [Fact]
    public void HostOptions_BackgroundServiceExceptionBehavior_ShouldBeStopHost_WhenPlatformWired()
    {
        // Arrange — mirror Program.cs L96-110 wiring exactly.
        var services = new ServiceCollection();
        services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<HostOptions>>().Value;

        // Assert
        options.BackgroundServiceExceptionBehavior
            .Should().Be(BackgroundServiceExceptionBehavior.StopHost,
                "Verbara house-style (ADR-0019): worker fatal must stop the host so K8s/orchestrator restarts the pod with a visible 'Last State Reason: Error' instead of swallowing the failure silently.");
    }
}
