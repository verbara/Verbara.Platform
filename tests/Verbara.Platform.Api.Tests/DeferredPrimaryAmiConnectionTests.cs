using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// csat-completion (Platform/ADR-0020) — regression for the headless-boot crash: the voice CSAT
/// <see cref="IAmiConnection"/> is DEFERRED to first use so the host boots with no telephony configured.
/// The previous factory eagerly resolved the primary <c>VerbaraServer.Connection</c> and threw at
/// <c>Host.StartAsync</c> (the CsatRunnerOrchestrator constructs the voice adapter during start), which
/// killed every no-AMI boot — notably the CI OpenAPI-export capture. These tests lock the fail-at-use
/// contract at the wrapper level.
/// </summary>
public sealed class DeferredPrimaryAmiConnectionTests
{
    // An EMPTY pool is exactly the "no primary AMI server configured" state a headless host boots with:
    // GetServer("primary") returns null. Constructing the pool needs the factory + logger factory only;
    // no server is ever added, so no real AMI connection is attempted.
    private static VerbaraServerPool EmptyPool()
        => new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);

    [Fact]
    public void Constructor_ShouldNotResolvePrimaryOrThrow_WhenNoPrimaryServerConfigured()
    {
        // The whole point of the fix: constructing the wrapper (what DI does when the orchestrator builds
        // the voice adapter during Host.StartAsync) must NOT touch the pool and must NOT throw.
        var connection = new DeferredPrimaryAmiConnection(EmptyPool());

        Assert.NotNull(connection);
    }

    [Fact]
    public async Task SendActionAsync_ShouldThrowDescriptiveInvalidOperation_WhenNoPrimaryServerConfigured()
    {
        // First USE (a real voice CSAT dispatch) with no primary server → the descriptive throw fires here,
        // not at boot. This is the same message the old boot-time factory raised, now correctly deferred.
        var connection = new DeferredPrimaryAmiConnection(EmptyPool());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.SendActionAsync(Substitute.For<ManagerAction>()));

        Assert.Equal("No primary AMI server is configured for voice CSAT dispatch.", ex.Message);
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenNoPrimaryServerConfigured()
    {
        // Host shutdown disposes singletons. The wrapper owns no connection, so disposal must never resolve
        // the (absent) primary and throw — otherwise a headless host would fault on shutdown.
        var connection = new DeferredPrimaryAmiConnection(EmptyPool());

        await connection.DisposeAsync();
    }
}
