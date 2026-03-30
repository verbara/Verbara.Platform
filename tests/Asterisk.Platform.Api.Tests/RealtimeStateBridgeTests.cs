using System.Reactive.Subjects;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Pro.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Asterisk.Platform.Api.Tests;

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

    public RealtimeStateBridgeTests()
    {
        _syncService = Substitute.For<IRealtimeSyncService>();
        _ami = Substitute.For<IAmiConnection>();

        // Wire the real PlatformEventBus backed by our controllable subject
        _eventBus = new PlatformEventBus();
    }

    private RealtimeStateBridge CreateBridge(bool includeAmi = true)
    {
        var ami = includeAmi ? _ami : null;
        return new RealtimeStateBridge(
            _eventBus,
            _syncService,
            NullLogger<RealtimeStateBridge>.Instance,
            ami);
    }

    private async Task PublishAndWaitAsync(PlatformEvent evt, int delayMs = 50)
    {
        _eventBus.Publish(evt);
        await Task.Delay(delayMs); // allow async void OnEvent to complete
    }

    // ─── State helpers ────────────────────────────────────────────────────────

    private static AgentStateChangedEvent MakeEvent(string newState, string agentId = "a1", string tenantId = "t1") =>
        new(tenantId, agentId, "Agent One", "Available", newState);

    // ─── Test 1: Available → unpause ──────────────────────────────────────────

    [Fact]
    public async Task OnAgentStateChanged_Available_ShouldSyncUnpausedAndSendQueuePause_Unpaused()
    {
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(MakeEvent("Available"));

        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", false);
        await _ami.Received(1).SendActionAsync(
            Arg.Is<QueuePauseAction>(a => a.Paused == false && a.Interface == "PJSIP/t1-agent-a1" && a.Reason == "Available"));
    }

    // ─── Test 2: Break → pause ────────────────────────────────────────────────

    [Fact]
    public async Task OnAgentStateChanged_Break_ShouldSyncPausedAndSendQueuePause_Paused()
    {
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(MakeEvent("Break"));

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
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(MakeEvent(state));

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

        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await PublishAndWaitAsync(MakeEvent("Break"));

        // AMI still receives the action despite DB failure
        await _ami.Received(1).SendActionAsync(Arg.Any<QueuePauseAction>());
    }

    // ─── Test 5: AMI failure — no exception propagates ───────────────────────

    [Fact]
    public async Task OnAgentStateChanged_AmiFailure_ShouldNotPropagateException()
    {
        _ami.SendActionAsync(Arg.Any<QueuePauseAction>())
            .ThrowsAsync(new TimeoutException("AMI unreachable"));

        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        // Should not throw
        var exception = await Record.ExceptionAsync(() => PublishAndWaitAsync(MakeEvent("Available")));

        exception.Should().BeNull();
        // DB sync still happened
        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", false);
    }

    // ─── Test 6: Non-AgentStateChangedEvent → ignored ────────────────────────

    [Fact]
    public async Task OnEvent_NonAgentStateChangedEvent_ShouldIgnoreAndNotCallSync()
    {
        var bridge = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        var otherEvent = new ConversationAssignedEvent("t1", "conv-1", "a1", "support", "voice", "Customer");
        await PublishAndWaitAsync(otherEvent);

        await _syncService.DidNotReceive().SyncAgentPausedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _ami.DidNotReceive().SendActionAsync(Arg.Any<QueuePauseAction>());
    }

    // ─── Test 7: AMI null — only DB write, no exception ──────────────────────

    [Fact]
    public async Task OnAgentStateChanged_AmiNull_ShouldOnlySyncDb_NoException()
    {
        var bridge = CreateBridge(includeAmi: false);
        await bridge.StartAsync(CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => PublishAndWaitAsync(MakeEvent("Break")));

        exception.Should().BeNull();
        await _syncService.Received(1).SyncAgentPausedAsync("t1", "a1", true);
        // _ami is never called — it was not injected
        await _ami.DidNotReceive().SendActionAsync(Arg.Any<QueuePauseAction>());
    }

    public void Dispose()
    {
        _subject.Dispose();
        _eventBus.Dispose();
    }
}
