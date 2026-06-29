using System.Reactive.Subjects;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Realtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Unit tests for <see cref="RealtimeStateBridge"/>.
/// Drives the bridge directly without WebApplicationFactory.
/// </summary>
public sealed class RealtimeStateBridgeTests : IDisposable
{
    private readonly Subject<PlatformEvent> _subject = new();
    private readonly PlatformEventBus _eventBus;
    private readonly IRealtimeSyncService _syncService;
    private readonly IAmiConnection _ami;
    private readonly VerbaraServerPool _serverPool;

    public RealtimeStateBridgeTests()
    {
        _syncService = Substitute.For<IRealtimeSyncService>();
        _ami = Substitute.For<IAmiConnection>();

        // Create a real pool backed by mock factories
        var connectionFactory = Substitute.For<IAmiConnectionFactory>();
        _serverPool = new VerbaraServerPool(connectionFactory, NullLoggerFactory.Instance);

        // Wire the real PlatformEventBus backed by our controllable subject
        _eventBus = new PlatformEventBus();
    }

    private RealtimeStateBridge CreateBridge()
    {
        return new RealtimeStateBridge(
            _eventBus,
            _syncService,
            _serverPool,
            NullLogger<RealtimeStateBridge>.Instance);
    }

    private void AddPrimaryServer()
    {
        var server = new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance);
        _serverPool.AddExistingServer("primary", server);
    }

    // Deterministic fence: await the bridge's awaitable handler seam directly, so the call returns
    // only after the DB sync + AMI QueuePause side-effects have run. The Rx subscription wiring
    // (Events.Subscribe(OnEvent) -> HandleEventAsync) is covered separately by the OnEvent_…_When
    // PublishedThroughEventBus test below, which drives the real _eventBus dispatch path.
    private static Task PublishAndWaitAsync(RealtimeStateBridge bridge, PlatformEvent evt) =>
        bridge.HandleEventAsync(evt);

    // ─── State helpers ────────────────────────────────────────────────────────

    private static AgentStateChangedEvent MakeEvent(string newState, string agentId = "a1", string tenantId = "t1") =>
        new(tenantId, agentId, "Agent One", "Available", newState);

    // ─── Test 1: Rx wiring — publish through the real event bus ──────────────
    // Retained as the one test that exercises the actual Rx dispatch path
    // (_eventBus.Events.Subscribe(OnEvent) -> HandleEventAsync). Deterministic via a
    // TaskCompletionSource the substituted _syncService stub pulses when invoked, so we never
    // sleep on a wall clock — we await the causal signal that OnEvent routed to the handler.

    [Fact]
    public async Task OnEvent_ShouldRouteToHandlerAndSyncUnpaused_WhenPublishedThroughEventBus()
    {
        var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        _syncService.SyncAgentPausedAsync("t1", "a1", false)
            .Returns(_ => { synced.TrySetResult(); return ValueTask.CompletedTask; });
#pragma warning restore CA2012

        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        // Publish through the real _eventBus — the subscription's synchronous OnNext (OnEvent)
        // must route to HandleEventAsync fire-and-forget.
        _eventBus.Publish(MakeEvent("Available"));

        await synced.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", false);
        await _ami.Received(1).SendActionAsync(
            Arg.Is<QueuePauseAction>(a => a.Paused == false && a.Interface == "PJSIP/t1-agent-a1" && a.Reason == "Available"));
    }

    // ─── Test 2: Break → pause ────────────────────────────────────────────────

    [Fact]
    public async Task OnAgentStateChanged_Break_ShouldSyncPausedAndSendQueuePause_Paused()
    {
        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(bridge, MakeEvent("Break"));

        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", true);
        await _ami.Received(1).SendActionAsync(
            Arg.Is<QueuePauseAction>(a => a.Paused == true && a.Reason == "Break"));
    }

    // ─── Test 3: All 8 states ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Available", false)]
    [InlineData("Busy", false)]
    [InlineData("Break", true)]
    [InlineData("Lunch", true)]
    [InlineData("Training", true)]
    [InlineData("ACW", true)]
    [InlineData("DND", true)]
    [InlineData("Offline", true)]
    public async Task OnAgentStateChanged_AllStates_ShouldSyncCorrectPausedValue(string state, bool expectedPaused)
    {
        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(bridge, MakeEvent(state));

        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", expectedPaused);
        await _ami.Received(1).SendActionAsync(
            Arg.Is<QueuePauseAction>(a => a.Paused == expectedPaused));
    }

    // ─── Test 4: DB failure — AMI still called ────────────────────────────────

    [Fact]
    public async Task OnAgentStateChanged_DbFailure_ShouldStillCallAmi()
    {
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        _syncService.SyncAgentPausedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(callInfo => new ValueTask(Task.FromException(new InvalidOperationException("DB unavailable"))));
#pragma warning restore CA2012

        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(bridge, MakeEvent("Break"));

        // AMI still receives the action despite DB failure
        await _ami.Received(1).SendActionAsync(Arg.Any<QueuePauseAction>());
    }

    // ─── Test 5: AMI failure — no exception propagates ───────────────────────

    [Fact]
    public async Task OnAgentStateChanged_AmiFailure_ShouldNotPropagateException()
    {
        _ami.SendActionAsync(Arg.Any<QueuePauseAction>())
            .ThrowsAsync(new TimeoutException("AMI unreachable"));

        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        // Should not throw
        var exception = await Record.ExceptionAsync(() => PublishAndWaitAsync(bridge, MakeEvent("Available")));

        exception.Should().BeNull();
        // DB sync still happened
        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", false);
    }

    // ─── Test 6: Non-AgentStateChangedEvent → ignored ────────────────────────

    [Fact]
    public async Task OnEvent_NonAgentStateChangedEvent_ShouldIgnoreAndNotCallSync()
    {
        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        var otherEvent = new ConversationAssignedEvent("t1", "conv-1", "a1", "support", "voice", "Customer");
        await PublishAndWaitAsync(bridge, otherEvent);

        await _syncService.DidNotReceive().SyncAgentPausedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _ami.DidNotReceive().SendActionAsync(Arg.Any<QueuePauseAction>());
    }

    // ─── Test 7: No server in pool — only DB write, no exception ─────────────

    [Fact]
    public async Task OnAgentStateChanged_NoServerInPool_ShouldOnlySyncDb_NoException()
    {
        // Don't add any server to pool — simulates cluster not yet connected
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => PublishAndWaitAsync(bridge, MakeEvent("Break")));

        exception.Should().BeNull();
        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", true);
        // _ami is never called — no server in pool
        await _ami.DidNotReceive().SendActionAsync(Arg.Any<QueuePauseAction>());
    }

    // ─── W4: deferred-pause pending event ─────────────────────────────────────

    [Fact]
    public async Task OnEvent_ShouldQueuePauseTrue_WhenPendingStateSet()
    {
        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(bridge, new AgentPendingStateChangedEvent("t1", "a1", "Agent One", "Break"));

        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", true);
        await _ami.Received(1).SendActionAsync(
            Arg.Is<QueuePauseAction>(a => a.Paused == true && a.Interface == "PJSIP/t1-agent-a1" && a.Reason == "Break"));
    }

    [Fact]
    public async Task OnEvent_ShouldQueuePauseFalse_WhenPendingStateCleared()
    {
        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(bridge, new AgentPendingStateChangedEvent("t1", "a1", "Agent One", PendingState: null));

        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", false);
        await _ami.Received(1).SendActionAsync(
            Arg.Is<QueuePauseAction>(a => a.Paused == false && a.Interface == "PJSIP/t1-agent-a1" && a.Reason == "pause_cancelled"));
    }

    [Fact]
    public async Task OnEvent_ShouldStillPause_WhenAgentStateChangedToNonRoutable()
    {
        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(bridge, MakeEvent("Break"));

        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", true);
        await _ami.Received(1).SendActionAsync(
            Arg.Is<QueuePauseAction>(a => a.Paused == true && a.Reason == "Break"));
    }

    [Fact]
    public async Task OnEvent_ShouldStillUnpause_WhenAgentStateChangedToRoutable()
    {
        AddPrimaryServer();
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(bridge, MakeEvent("Available"));

        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", false);
        await _ami.Received(1).SendActionAsync(
            Arg.Is<QueuePauseAction>(a => a.Paused == false && a.Reason == "Available"));
    }

    public void Dispose()
    {
        _subject.Dispose();
        _eventBus.Dispose();
    }
}
