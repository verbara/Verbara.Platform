using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using OriginateResult = Verbara.Sdk.Pro.Dialer.Execution.OriginateResult;
using H = Verbara.Platform.Api.Tests.SupervisorListen.ListenHarness;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen (verifier findings 1 and 2): once a listen has a side effect — a reserved slot, an
/// originate that may have reached Asterisk, an ended registry entry — the request's cancellation never skips its
/// cleanup or its audit. An accepted listen is audited <c>supervision.listen_started</c> exactly once and every
/// started listen <c>supervision.listen_stopped</c> exactly once, in that order; a listen that was never accepted is
/// not audited at all, and a late leg of a failed originate is hung up the moment it appears.
/// </summary>
public sealed class SupervisorListenLifecycleTests : IAsyncDisposable
{
    private const string Started = SupervisorListenAudit.Started;
    private const string Stopped = SupervisorListenAudit.Stopped;
    private const string LegChannel = "PJSIP/acme-agent-sup-7d2c9e4b1a5f4c3e8b6d0f1a2c3e4b5d-00000009";

    private readonly H _h = new();
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly VerbaraServerPool _pool = new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
    private readonly SupervisorListenObserver _observer;
    private readonly CancellationTokenSource _request = new();

    public SupervisorListenLifecycleTests()
    {
        _pool.AddExistingServer("primary", new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));
        _ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>()).Returns(new ManagerResponse { Response = "Success" });
        _observer = new SupervisorListenObserver(_pool, _h.Registry, _h.AuditWriter(), _h.Clock, NullLogger<SupervisorListenObserver>.Instance);
        _h.AuditHonoursCancellation();
    }

    public async ValueTask DisposeAsync()
    {
        _observer.Dispose();
        _request.Dispose();
        await _pool.DisposeAsync();
    }

    private SupervisorListenService ServiceWithObserver() => new(
        _h.Conversations, _h.Profiles, _h.CallLegs, _h.Executor, _h.Resolver(), _h.Tenants, _h.Leader, _h.Clock,
        _h.Registry, _h.AuditWriter(), _observer, NullLogger<SupervisorListenService>.Instance);

    private static ManagerEvent Event(string name, params (string Key, string Value)[] fields)
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal) { ["Event"] = name };
        foreach (var (key, value) in fields)
            raw[key] = value;
        return new ManagerEvent { RawFields = raw };
    }

    private string SentListenId => _h.Executor.Actions.Single().ChannelId!;

    /// <summary>The audit actions written, in order (writes given a now-cancelled token are not counted).</summary>
    private List<string> AuditTrail() =>
    [
        .. _h.Audit.ReceivedCalls()
            .Where(c => c.GetArguments() is { Length: 13 } args && args[12] is CancellationToken { IsCancellationRequested: false })
            .Select(c => (string)c.GetArguments()[2]!),
    ];

    // ─── Finding 1: the request's cancellation ──────────────────────────────────

    [Fact]
    public async Task Start_ShouldAuditListenStartedOnce_WhenTheRequestIsCancelledAfterTheOriginateIsAccepted()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        _h.Executor.During = _ => _request.Cancel();

        var result = await _h.Service().StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", _request.Token);

        result.Listen.Should().NotBeNull("Asterisk accepted the originate, so the listen exists");
        _h.AuditCount(Started).Should().Be(1, "an accepted listen is audited even when its request was cancelled");
        _h.Registry.GetBySupervisor(H.TenantId, H.Supervisor).Should().NotBeNull();
    }

    [Fact]
    public async Task Start_ShouldReleaseTheSlot_ArmTheLegsHangup_AndAuditNothing_WhenCancelledDuringTheOriginate()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        _h.Executor.During = ct =>
        {
            _request.Cancel();
            ct.ThrowIfCancellationRequested();
        };

        var act = () => _h.Service().StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", _request.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _h.Registry.GetBySupervisor(H.TenantId, H.Supervisor).Should().BeNull("a cancelled start leaves no reservation");
        _h.LegControl.HungUp.Select(l => l.ListenId).Should().Equal([SentListenId], "the originate may have reached Asterisk");
        AuditTrail().Should().BeEmpty("a listen that was never accepted is never audited");
    }

    [Fact]
    public async Task Start_ShouldReleaseTheSlot_AndArmNoHangup_WhenCancelledBeforeTheOriginateIsSent()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedPhoneAsync();
        _h.Tenants.GetAsync(H.Tenant, Arg.Any<CancellationToken>()).ThrowsAsync(_ =>
        {
            _request.Cancel();
            return new OperationCanceledException(_request.Token);
        });

        var act = () => _h.Service().StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "phone", _request.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _h.Registry.GetBySupervisor(H.TenantId, H.Supervisor).Should().BeNull("the slot is released");
        _h.Executor.Actions.Should().BeEmpty();
        _h.LegControl.HungUp.Should().BeEmpty("nothing was dialed");
        AuditTrail().Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_ShouldHangUpAndAuditListenStoppedOnce_WhenTheRequestIsCancelled()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var service = _h.Service();
        var listen = (await service.StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None)).Listen!;
        await _request.CancelAsync();

        var stopped = await service.StopAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, _request.Token);

        stopped.Stopped!.ListenId.Should().Be(listen.ListenId);
        _h.LegControl.HungUp.Select(l => l.ListenId).Should().Equal(listen.ListenId);
        AuditTrail().Should().Equal(Started, Stopped);
        _h.Registry.GetBySupervisor(H.TenantId, H.Supervisor).Should().BeNull();
    }

    [Fact]
    public async Task StopDuringTheOriginate_ShouldAuditStartedThenStopped_ExactlyOnceEach()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var service = _h.Service();
        SupervisorListenStopResult? stop = null;
        _h.Executor.During = _ => stop = service
            .StopAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, CancellationToken.None).GetAwaiter().GetResult();

        await service.StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None);

        stop!.Stopped.Should().NotBeNull("a stop may end a listen that is still dialing");
        AuditTrail().Should().Equal([Started, Stopped], "never a stop before its start");
        _h.Audit.ReceivedCalls().Single(c => Equals(c.GetArguments()[2], Stopped)).GetArguments()[5]
            .Should().Be("user", "the stop is still attributed to the stopping caller");
    }

    // ─── Finding 2: originate-failed leaves nothing behind ──────────────────────

    public static TheoryData<string> FailedOriginates => ["timeout", "refused"];

    private void FailTheOriginate(string how)
    {
        if (how == "timeout")
            _h.Executor.Throw = new TimeoutException("no response to Originate within 10 s");
        else
            _h.Executor.Result = new OriginateResult(false, "act-1", "Originate failed");
    }

    [Theory]
    [MemberData(nameof(FailedOriginates))]
    public async Task OriginateFailed_ShouldHangUpALateLeg_AndAuditNothing(string how)
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        FailTheOriginate(how);

        var result = await ServiceWithObserver().StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None);
        result.Refusal!.Error.Should().Be("originate-failed");
        var listenId = SentListenId;

        await _observer.HandleAsync(Event("Newchannel", ("Uniqueid", listenId), ("Channel", LegChannel)));
        await _observer.HandleAsync(Event("ChanSpyStart", ("SpyerUniqueid", listenId), ("SpyerChannel", LegChannel)));
        await _observer.HandleAsync(Event("Hangup", ("Uniqueid", listenId), ("Channel", LegChannel)));

        await _ami.Received(1).SendActionAsync(Arg.Is<HangupAction>(a => a.Channel == LegChannel), Arg.Any<CancellationToken>());
        AuditTrail().Should().BeEmpty("a failed originate is neither started nor stopped");
        _h.Registry.Get(listenId).Should().BeNull();
        _h.Registry.GetBySupervisor(H.TenantId, H.Supervisor).Should().BeNull();
        _h.Registry.PendingHangupCount.Should().Be(0, "the pending hangup was spent on the late leg");
    }

    [Fact]
    public async Task OriginateFailed_ShouldAuditNothing_WhenTheLegHungUpBeforeTheSendReturned()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        _h.Executor.Throw = new TimeoutException("no response to Originate within 10 s");
        _h.Executor.During = _ => _observer.HandleAsync(Event("Hangup", ("Uniqueid", SentListenId))).GetAwaiter().GetResult();

        var result = await ServiceWithObserver().StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None);

        result.Refusal!.Error.Should().Be("originate-failed");
        AuditTrail().Should().BeEmpty("the observer never audits the end of a listen that was not accepted");
    }

    [Fact]
    public async Task AcceptedOriginate_ShouldAuditStartedThenStopped_WhenTheLegEndedBeforeTheSendReturned()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        _h.Executor.During = _ => _observer.HandleAsync(Event("Hangup", ("Uniqueid", SentListenId))).GetAwaiter().GetResult();

        await ServiceWithObserver().StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None);

        AuditTrail().Should().Equal(Started, Stopped);
        var stopped = _h.Audit.ReceivedCalls().Single(c => Equals(c.GetArguments()[2], Stopped)).GetArguments();
        stopped[5].Should().Be("system", "a natural end names the system");
        ((IReadOnlyDictionary<string, string>)stopped[10]!)["end_reason"].Should().Be("not-answered");
    }
}
