using System.Net;
using System.Runtime.CompilerServices;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// A stopped and disposed API host must become collectable once a request has gone through the
/// rate-limiting stage of its pipeline. A request limiter whose replenishment timer outlives the host keeps
/// the timer queue pointing at the limiter, its policies and, through them, the host's whole service
/// provider, so every host a test run builds stays in memory until the process exits (#586).
/// </summary>
public sealed class RateLimiterHostLifetimeTests
{
    [Fact]
    public async Task HostDispose_ShouldReleaseTheHost_WhenARequestWentThroughTheRateLimiter()
    {
        // OpenTelemetry starts one process-wide self-diagnostics refresher the first time a host builds its
        // telemetry. That loop keeps the execution context it started on, and with it that one host, for the
        // life of the process. A first host absorbs that one-off, so the host under test is never the first.
        await ServeOneRequestAndDisposeAsync();

        var hostServices = await ServeOneRequestAndDisposeAsync();

        // The continuation after an await can run inline on the stack that completed the awaited task, where
        // that method's frame still references the factory. Each retry first waits, so collection runs on a
        // fresh stack; a limiter whose timer still holds the host keeps it alive on every attempt.
        for (var attempt = 0; attempt < 10 && hostServices.IsAlive; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100)); // fence-allow: SETTLE — collect off the stack that still references the factory
            for (var i = 0; i < 2; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
            }
        }

        hostServices.IsAlive.Should().BeFalse(
            "a disposed host must not stay reachable through anything its request pipeline created");
    }

    // Not inlined, so no local of the caller's frame keeps the factory, its client or its provider alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ServeOneRequestAndDisposeAsync()
    {
        var factory = new UnifiedPlatformApiFactory();
        WeakReference hostServices;
        try
        {
            // A route with a rate-limiting policy, so the limiter really creates a partition (and its
            // replenishment timer) for this host. Anonymous on purpose: the limiter grants the permit before
            // authentication answers 401, so no conversation needs to exist.
            using (var client = factory.CreateClient())
            using (var response = await client.PostAsync(
                $"/api/conversations/{Guid.NewGuid()}/typification-suggestion", content: null))
            {
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }

            hostServices = new WeakReference(factory.Services);
        }
        finally
        {
            await factory.DisposeAsync();
        }

        return hostServices;
    }
}
