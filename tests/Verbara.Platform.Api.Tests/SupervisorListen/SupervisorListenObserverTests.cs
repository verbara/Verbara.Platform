using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using H = Verbara.Platform.Api.Tests.SupervisorListen.ListenHarness;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 2.5 (design D6, D10): the AMI lifetime observer, fed <see cref="ManagerEvent"/>s with
/// <c>RawFields</c>. It follows the supervisor leg by its uniqueid (= the listen id), ends the listen exactly once with
/// the right reason, hangs up only that leg on a stop, and subscribes on first use only.
/// </summary>
public sealed class SupervisorListenObserverTests : IAsyncDisposable
{
    private const string LegChannel = "PJSIP/acme-agent-sup-7d2c9e4b1a5f4c3e8b6d0f1a2c3e4b5d-00000007";

    private readonly H _h = new();
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly VerbaraServerPool _pool = new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
    private readonly SupervisorListenObserver _observer;

    public SupervisorListenObserverTests()
    {
        _pool.AddExistingServer("primary", new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));
        _ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>()).Returns(new ManagerResponse { Response = "Success" });
        _observer = new SupervisorListenObserver(_pool, _h.Registry, _h.AuditWriter(), _h.Clock, NullLogger<SupervisorListenObserver>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _observer.Dispose();
        await _pool.DisposeAsync();
    }

    private static ManagerEvent Event(string name, params (string Key, string Value)[] fields)
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal) { ["Event"] = name };
        foreach (var (key, value) in fields)
            raw[key] = value;
        return new ManagerEvent { RawFields = raw };
    }

    private async Task<string> StartListenAsync()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var result = await _h.Service().StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None);
        return result.Listen!.ListenId;
    }

    private SupervisorListenService ServiceWithObserver() => new(
        _h.Conversations, _h.Profiles, _h.CallLegs, _h.Executor, _h.Resolver(), _h.Tenants, _h.Leader, _h.Clock,
        _h.Registry, _h.AuditWriter(), _observer, NullLogger<SupervisorListenService>.Instance);

    private Task StoppedAudits(int times, string reason, string actorType) =>
        _h.Audit.Received(times).RecordAsync(
            H.TenantId, "conversations", "supervision.listen_stopped", "info", Arg.Any<string>(), actorType,
            Arg.Any<string?>(), "Conversation", Arg.Any<Guid?>(), Arg.Any<Verbara.Platform.Audit.AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m["end_reason"] == reason),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());

    private Task AnyStoppedAudits(int times) =>
        _h.Audit.Received(times).RecordAsync(
            Arg.Any<Verbara.Platform.Core.TenantId>(), "conversations", "supervision.listen_stopped", Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
            Arg.Any<Verbara.Platform.Audit.AuditChanges?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());

    [Fact]
    public async Task Newchannel_ShouldRecordTheLegsChannelName()
    {
        var listenId = await StartListenAsync();

        await _observer.HandleAsync(Event("Newchannel", ("Uniqueid", listenId), ("Channel", LegChannel)));

        _h.Registry.Get(listenId)!.ChannelName.Should().Be(LegChannel);
    }

    [Fact]
    public async Task ChanSpyStart_ShouldMoveTheListenToListening()
    {
        var listenId = await StartListenAsync();
        _h.Clock.UtcNow = _h.Clock.UtcNow.AddSeconds(2);

        await _observer.HandleAsync(Event("ChanSpyStart", ("SpyerUniqueid", listenId), ("SpyerChannel", LegChannel)));

        var listen = _h.Registry.Get(listenId)!;
        listen.State.Should().Be("listening");
        listen.ListeningSince.Should().Be(_h.Clock.UtcNow);
    }

    [Fact]
    public async Task Hangup_ShouldEndAsNotAnswered_BeforeChanSpyStart()
    {
        var listenId = await StartListenAsync();

        await _observer.HandleAsync(Event("Hangup", ("Uniqueid", listenId), ("Channel", LegChannel)));

        _h.Registry.Get(listenId).Should().BeNull();
        await StoppedAudits(1, "not-answered", "system");
    }

    [Fact]
    public async Task Hangup_ShouldEndAsCallEnded_AfterChanSpyStart_WithTheSystemAsActor()
    {
        var listenId = await StartListenAsync();
        await _observer.HandleAsync(Event("ChanSpyStart", ("SpyerUniqueid", listenId)));
        _h.Clock.UtcNow = _h.Clock.UtcNow.AddSeconds(7);

        await _observer.HandleAsync(Event("Hangup", ("Uniqueid", listenId)));

        await _h.Audit.Received(1).RecordAsync(
            H.TenantId, "conversations", "supervision.listen_stopped", "info", "system", "system",
            Arg.Any<string?>(), "Conversation", Arg.Any<Guid?>(), Arg.Any<Verbara.Platform.Audit.AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m["end_reason"] == "call-ended" && m["listened_seconds"] == "7"),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Events_OfOtherChannels_ShouldBeIgnored()
    {
        var listenId = await StartListenAsync();

        await _observer.HandleAsync(Event("Hangup", ("Uniqueid", "1712345678.42")));
        await _observer.HandleAsync(Event("ChanSpyStart", ("SpyerUniqueid", "someone-else")));

        _h.Registry.Get(listenId)!.State.Should().Be("dialing");
        await AnyStoppedAudits(0);
    }

    [Fact]
    public async Task Stop_ShouldHangUpTheRecordedChannel_AndAuditExactlyOnce()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var service = ServiceWithObserver();
        var listenId = (await service.StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None)).Listen!.ListenId;
        await _observer.HandleAsync(Event("Newchannel", ("Uniqueid", listenId), ("Channel", LegChannel)));
        await _observer.HandleAsync(Event("ChanSpyStart", ("SpyerUniqueid", listenId)));

        await service.StopAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, CancellationToken.None);
        await _observer.HandleAsync(Event("Hangup", ("Uniqueid", listenId)));

        await _ami.Received(1).SendActionAsync(Arg.Is<HangupAction>(a => a.Channel == LegChannel), Arg.Any<CancellationToken>());
        await _ami.DidNotReceive().SendActionAsync(Arg.Is<HangupAction>(a => a.Channel != LegChannel), Arg.Any<CancellationToken>());
        await StoppedAudits(1, "stopped", "user");
        await AnyStoppedAudits(1);
    }

    [Fact]
    public async Task Stop_BeforeTheLegExists_ShouldHangItUpWhenItAppears()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var service = ServiceWithObserver();
        var listenId = (await service.StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None)).Listen!.ListenId;

        await service.StopAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, CancellationToken.None);
        await _ami.DidNotReceive().SendActionAsync(Arg.Any<HangupAction>(), Arg.Any<CancellationToken>());

        await _observer.HandleAsync(Event("Newchannel", ("Uniqueid", listenId), ("Channel", LegChannel)));

        await _ami.Received(1).SendActionAsync(Arg.Is<HangupAction>(a => a.Channel == LegChannel), Arg.Any<CancellationToken>());
        _h.Registry.PendingHangupCount.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_ShouldEndAStaleDialingListen_AsNotAnswered()
    {
        var stale = await StartListenAsync();
        _h.Clock.UtcNow = _h.Clock.UtcNow.AddSeconds(31);

        await _observer.SweepAsync();
        await _observer.SweepAsync();

        _h.Registry.Get(stale).Should().BeNull();
        await StoppedAudits(1, "not-answered", "system");
    }

    [Fact]
    public async Task Sweep_ShouldKeepAFreshOrListeningListen()
    {
        var listenId = await StartListenAsync();
        await _observer.HandleAsync(Event("ChanSpyStart", ("SpyerUniqueid", listenId)));
        _h.Clock.UtcNow = _h.Clock.UtcNow.AddMinutes(10);

        await _observer.SweepAsync();

        _h.Registry.Get(listenId).Should().NotBeNull("a connected listen lasts as long as the call");
    }

    [Fact]
    public void Observer_ShouldSubscribeOnFirstUseOnly()
    {
        _ami.DidNotReceive().Subscribe(Arg.Any<IObserver<ManagerEvent>>());

        _observer.EnsureObserving();
        _observer.EnsureObserving();

        _ami.Received(1).Subscribe(_observer);
    }

    [Fact]
    public void Observer_ShouldNotThrow_WhenNoAmiServerIsConfigured()
    {
        var empty = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        using var observer = new SupervisorListenObserver(empty, _h.Registry, _h.AuditWriter(), _h.Clock, NullLogger<SupervisorListenObserver>.Instance);

        var act = () => observer.EnsureObserving();

        act.Should().NotThrow();
    }
}
