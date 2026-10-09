using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
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
    // sdk-2-7-0-pin-cascade: Sdk 2.7.0 added three IAmiConnection members with DEFAULT bodies
    // (ReportsEventActionOutcome => false, an outcome overload that drops the outcome, and a StateChanged
    // event that raises nothing). A wrapper that does not override them compiles clean and silently answers
    // with the defaults instead of the primary's. These tests seat a substituted primary and call the
    // wrapper TYPED AS IAmiConnection, so they fail whenever a forwarding is missing.
    private static (IAmiConnection Wrapper, IAmiConnection Primary) WrapperOverSubstitutedPrimary()
    {
        var primary = Substitute.For<IAmiConnection>();
        var pool = EmptyPool();
        pool.AddExistingServer("primary", new VerbaraServer(primary, NullLogger<VerbaraServer>.Instance));
        IAmiConnection wrapper = new DeferredPrimaryAmiConnection(pool);
        return (wrapper, primary);
    }

    [Fact]
    public void ReportsEventActionOutcome_ShouldReturnPrimaryAnswer_WhenPrimaryReportsOutcomes()
    {
        var (wrapper, primary) = WrapperOverSubstitutedPrimary();
        primary.ReportsEventActionOutcome.Returns(true);

        Assert.True(wrapper.ReportsEventActionOutcome);
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldDelegateOutcomeOverloadToPrimary_WhenOutcomeIsSupplied()
    {
        var (wrapper, primary) = WrapperOverSubstitutedPrimary();
        var action = Substitute.For<ManagerAction>();
        var outcome = new EventActionOutcome();
        using var cts = new CancellationTokenSource();
        var expected = new ManagerEvent { EventType = "QueueMember" };
        var enumerated = false;
        primary.SendEventGeneratingActionAsync(action, outcome, cts.Token).Returns(_ => Sequence());

        var received = new List<ManagerEvent>();
        await foreach (var evt in wrapper.SendEventGeneratingActionAsync(action, outcome, cts.Token))
        {
            received.Add(evt);
        }

        primary.Received(1).SendEventGeneratingActionAsync(action, outcome, cts.Token);
        primary.DidNotReceive().SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>());
        Assert.True(enumerated);
        Assert.Same(expected, Assert.Single(received));

        async IAsyncEnumerable<ManagerEvent> Sequence()
        {
            await Task.Yield();
            enumerated = true;
            yield return expected;
        }
    }

    [Fact]
    public void StateChanged_ShouldDeliverPrimaryChanges_WhenSubscribedThroughWrapper()
    {
        var (wrapper, primary) = WrapperOverSubstitutedPrimary();
        var change = new AmiConnectionStateChange(
            AmiConnectionState.Connected, AmiConnectionState.Reconnecting, cause: null, byCaller: false);
        var delivered = new List<AmiConnectionStateChange>();

        wrapper.StateChanged += delivered.Add;
        primary.StateChanged += Raise.Event<Action<AmiConnectionStateChange>>(change);

        Assert.Same(change, Assert.Single(delivered));
    }

    [Fact]
    public void StateChanged_ShouldStopDelivering_WhenUnsubscribedThroughWrapper()
    {
        var (wrapper, primary) = WrapperOverSubstitutedPrimary();
        var first = new AmiConnectionStateChange(
            AmiConnectionState.Connected, AmiConnectionState.Reconnecting, cause: null, byCaller: false);
        var second = new AmiConnectionStateChange(
            AmiConnectionState.Reconnecting, AmiConnectionState.Connected, cause: null, byCaller: false);
        var delivered = new List<AmiConnectionStateChange>();
        Action<AmiConnectionStateChange> handler = delivered.Add;

        wrapper.StateChanged += handler;
        primary.StateChanged += Raise.Event<Action<AmiConnectionStateChange>>(first);
        wrapper.StateChanged -= handler;
        primary.StateChanged += Raise.Event<Action<AmiConnectionStateChange>>(second);

        Assert.Same(first, Assert.Single(delivered));
    }

    // sdk-2-8-0-pin-cascade: Sdk 2.8.0 (#391) added Subscribe(Func<ManagerEvent, CancellationToken, ValueTask>)
    // with a default body that attaches the handler through the wrapper's own OnEvent with a token that is never
    // cancelled. The wrapper must hand it to the primary instead, so the primary's fast close reaches the handler.
    [Fact]
    public void Subscribe_ShouldDelegateTokenHandlerToPrimary_WhenHandlerIsSubscribedThroughWrapper()
    {
        var (wrapper, primary) = WrapperOverSubstitutedPrimary();
        var subscription = Substitute.For<IDisposable>();
        Func<ManagerEvent, CancellationToken, ValueTask> handler = (_, _) => ValueTask.CompletedTask;
        primary.Subscribe(handler).Returns(subscription);

        var returned = wrapper.Subscribe(handler);

        Assert.Same(subscription, returned);
        primary.Received(1).Subscribe(handler);
    }
}
