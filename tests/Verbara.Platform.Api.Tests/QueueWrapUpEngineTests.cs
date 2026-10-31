using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The wrap-up scenarios through the provider wiring: the real session manager, resolved from the Sdk's registration
/// with Platform's provider and decorator, over a substituted AMI connection whose frames are written by hand. The
/// clock is real, because the manager's constructor that takes a <see cref="TimeProvider"/> is internal to the Sdk.
/// </summary>
public sealed class QueueWrapUpEngineTests
{
    private const string Tenant = "acme-eu";
    private const string Sales = "sales";
    private const string AsteriskSales = "acme-eu-sales";

    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task AgentComplete_ShouldCloseTheWrapUpInTheSameInstant_WhenTheQueueIsConfiguredWithZero()
    {
        await using var rig = await QueueWrapUpEngineRig.StartAsync();
        await rig.SeedAsync(Tenant, "q-1", Sales, seconds: 0);
        await rig.RefreshAsync();
        rig.Provider.GetWrapUpDuration(QueueWrapUpEngineRig.ServerId, AsteriskSales).Should().Be(
            TimeSpan.Zero, "zero is the queue's value, not the absence of one");
        rig.ConnectCall(AsteriskSales, "c1", "m1");

        rig.CompleteCall(AsteriskSales, "c1", "m1");

        var wrapUp = rig.WrapUpsRecorded.Should().ContainSingle(
            "a wrap-up of zero closes on the delivery that opened it").Subject;
        wrapUp.WrapUpDuration.Should().Be(TimeSpan.Zero);
        wrapUp.QueueName.Should().Be(AsteriskSales);
    }

    [Fact]
    public async Task AgentComplete_ShouldKeepTheOpenDuration_WhenTheQueueIsSavedDuringTheWrapUp()
    {
        await using var rig = await QueueWrapUpEngineRig.StartAsync();
        await rig.SeedAsync(Tenant, "q-1", Sales, seconds: 2);
        await rig.RefreshAsync();
        rig.ConnectCall(AsteriskSales, "c1", "m1");
        rig.CompleteCall(AsteriskSales, "c1", "m1");

        rig.Queues.Should().BeOfType<RealtimeSyncingQueueStore>();
        await rig.Queues.SaveAsync(QueueWrapUpRefresherRig.Queue(Tenant, "q-1", Sales, 3), CancellationToken.None);

        rig.WrapUpsRecorded.Should().BeEmpty("the save must land while the wrap-up is open");
        rig.EndCall("c1");
        var open = await rig.NextWrapUpAsync();
        open.WrapUpDuration.Should().BeCloseTo(
            TimeSpan.FromSeconds(2), Tolerance, "an open wrap-up keeps the length it opened with");

        rig.ConnectCall(AsteriskSales, "c2", "m2");
        rig.CompleteCall(AsteriskSales, "c2", "m2");
        rig.EndCall("c2");
        var next = await rig.NextWrapUpAsync();
        next.WrapUpDuration.Should().BeCloseTo(
            TimeSpan.FromSeconds(3), Tolerance, "the next wrap-up opens with the saved length");
    }

    [Fact]
    public async Task RefreshAsync_ShouldLoadTheSameAnswer_WhenOnlyOneQueueForcesWrapUp()
    {
        await using var rig = await QueueWrapUpEngineRig.StartAsync();
        await rig.SeedAsync(Tenant, "q-1", "forced", seconds: 12, forceWrapUp: true);
        await rig.SeedAsync(Tenant, "q-2", "plain", seconds: 12);

        await rig.RefreshAsync();

        rig.Provider.GetWrapUpDuration(QueueWrapUpEngineRig.ServerId, "acme-eu-forced").Should().Be(
            TimeSpan.FromSeconds(12));
        rig.Provider.GetWrapUpDuration(QueueWrapUpEngineRig.ServerId, "acme-eu-plain").Should().Be(
            TimeSpan.FromSeconds(12));
    }
}

/// <summary>
/// One server attached to the session manager that the service container builds, with Platform's stores, decorator,
/// provider and refresher registered as the host registers them. Frames go to the event observer the server
/// subscribed, so they pass through the production switch. Their shapes follow the Sdk's own wrap-up tests, which
/// are built on frames captured from Asterisk.
/// </summary>
internal sealed class QueueWrapUpEngineRig : IAsyncDisposable, IObserver<SessionDomainEvent>
{
    public const string ServerId = "primary";

    private const string Member = "PJSIP/agent1";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly ServiceProvider _services;
    private readonly VerbaraServer _server;
    private readonly IObserver<ManagerEvent> _frames;
    private readonly IDisposable _subscription;
    private readonly IQueueStore _seedQueues;
    private readonly Channel<CallWrapUpEvent> _pending = Channel.CreateUnbounded<CallWrapUpEvent>();
    private readonly Lock _gate = new();
    private readonly List<CallWrapUpEvent> _wrapUps = [];

    private QueueWrapUpEngineRig(
        ServiceProvider services, CallSessionManager manager, VerbaraServer server, IObserver<ManagerEvent> frames)
    {
        _services = services;
        _server = server;
        _frames = frames;
        _seedQueues = services.GetRequiredKeyedService<IQueueStore>(RealtimeSyncingStoresExtensions.QueueStoreInner);
        _subscription = manager.Events.Subscribe(this);
    }

    public IWrapUpDurationProvider Provider => _services.GetRequiredService<IWrapUpDurationProvider>();

    public IQueueStore Queues => _services.GetRequiredService<IQueueStore>();

    public IReadOnlyList<CallWrapUpEvent> WrapUpsRecorded
    {
        get
        {
            lock (_gate)
            {
                return [.. _wrapUps];
            }
        }
    }

    public static async Task<QueueWrapUpEngineRig> StartAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryStorage();
        services.AddSingleton(Substitute.For<IRealtimeSyncService>());
        services.AddRealtimeSyncingStores();
        services.AddLicensedAgentMetering();
        services.AddVerbaraSessionsMultiServer(o => o.WrapUpDuration = TimeSpan.FromSeconds(WrapUpConfig.FallbackSeconds));
        var provider = services.BuildServiceProvider();

        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("22.10.1");
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => NoEvents());
        IObserver<ManagerEvent>? observer = null;
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(o => observer = o))
            .Returns(Substitute.For<IDisposable>());

        var manager = (CallSessionManager)provider.GetRequiredService<ICallSessionManager>();
        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        manager.AttachToServer(server, ServerId);
        await server.StartAsync();

        return new QueueWrapUpEngineRig(
            provider, manager, server,
            observer ?? throw new InvalidOperationException("VerbaraServer.StartAsync subscribed no event observer."));
    }

    /// <summary>Writes a queue past the decorator, so its value can reach the provider only through a refresh.</summary>
    public async Task SeedAsync(string tenantId, string queueId, string name, int seconds, bool forceWrapUp = false)
    {
        await _services.GetRequiredService<ITenantStore>().UpsertAsync(new Tenant
        {
            TenantId = tenantId, Name = tenantId, Type = TenantType.Customer, Status = TenantStatus.Active,
        });

        var queue = QueueWrapUpRefresherRig.Queue(tenantId, queueId, name, seconds);
        queue.WrapUp.ForceWrapUp = forceWrapUp;
        await _seedQueues.SaveAsync(queue, CancellationToken.None);
    }

    public Task RefreshAsync() =>
        _services.GetRequiredService<QueueWrapUpDurationRefresher>().RefreshAsync(CancellationToken.None);

    /// <summary>The caller's channel, its join of the queue, the member's leg, the leave, the connect, the bridge.</summary>
    public void ConnectCall(string queue, string caller, string memberLeg)
    {
        var callerChannel = CallerChannel(caller);
        var memberChannel = MemberChannel(memberLeg);
        var bridge = Bridge(caller);
        Deliver(
            NewChannel(caller, callerChannel, "Ring", caller, "from-trunk", "500"),
            new QueueCallerJoinEvent
            {
                EventType = "QueueCallerJoin", UniqueId = caller, LinkedId = caller, Position = 1,
                RawFields = new Dictionary<string, string>
                {
                    ["Queue"] = queue, ["Channel"] = callerChannel, ["CallerIDNum"] = "5551234",
                    ["Uniqueid"] = caller, ["Linkedid"] = caller, ["Position"] = "1",
                },
            },
            NewChannel(memberLeg, memberChannel, "Down", caller, "agents", null),
            new NewStateEvent { EventType = "Newstate", UniqueId = memberLeg, ChannelState = "Up" },
            new QueueCallerLeaveEvent
            {
                EventType = "QueueCallerLeave", UniqueId = caller, LinkedId = caller, Position = 1,
                RawFields = new Dictionary<string, string>
                {
                    ["Queue"] = queue, ["Channel"] = callerChannel, ["Uniqueid"] = caller, ["Linkedid"] = caller,
                },
            },
            new AgentConnectEvent
            {
                EventType = "AgentConnect", UniqueId = caller, Channel = callerChannel, LinkedId = caller,
                Interface = Member, HoldTime = 0, DestUniqueId = memberLeg, DestChannel = memberChannel,
                DestLinkedId = caller,
                RawFields = new Dictionary<string, string>
                {
                    ["Channel"] = callerChannel, ["Uniqueid"] = caller, ["Linkedid"] = caller,
                    ["DestChannel"] = memberChannel, ["DestUniqueid"] = memberLeg, ["DestLinkedid"] = caller,
                    ["Queue"] = queue, ["Interface"] = Member, ["MemberName"] = Member, ["HoldTime"] = "0",
                },
            },
            new NewStateEvent { EventType = "Newstate", UniqueId = caller, ChannelState = "Up" },
            new BridgeCreateEvent
            {
                EventType = "BridgeCreate", BridgeUniqueid = bridge, BridgeType = "basic",
                BridgeTechnology = "simple_bridge",
            },
            new BridgeEnterEvent { EventType = "BridgeEnter", BridgeUniqueid = bridge, UniqueId = memberLeg },
            new BridgeEnterEvent { EventType = "BridgeEnter", BridgeUniqueid = bridge, UniqueId = caller });
    }

    /// <summary>The member hangs up: its leg leaves and ends, then app_queue reports <c>AgentComplete</c>.</summary>
    public void CompleteCall(string queue, string caller, string memberLeg)
    {
        var callerChannel = CallerChannel(caller);
        var memberChannel = MemberChannel(memberLeg);
        Deliver(
            new BridgeLeaveEvent { EventType = "BridgeLeave", BridgeUniqueid = Bridge(caller), UniqueId = memberLeg },
            new HangupEvent { EventType = "Hangup", UniqueId = memberLeg, Cause = 16 },
            new AgentCompleteEvent
            {
                EventType = "AgentComplete", UniqueId = caller, Channel = callerChannel, LinkedId = caller,
                Interface = Member, HoldTime = 0, TalkTime = 60, Reason = "agent", DestUniqueId = memberLeg,
                DestChannel = memberChannel, DestLinkedId = caller,
                RawFields = new Dictionary<string, string>
                {
                    ["Channel"] = callerChannel, ["Uniqueid"] = caller, ["Linkedid"] = caller,
                    ["DestChannel"] = memberChannel, ["DestUniqueid"] = memberLeg, ["DestLinkedid"] = caller,
                    ["Queue"] = queue, ["Interface"] = Member, ["MemberName"] = Member, ["HoldTime"] = "0",
                    ["TalkTime"] = "60", ["Reason"] = "agent",
                },
            });
    }

    /// <summary>The caller's leg, last out after the member's <c>AgentComplete</c>.</summary>
    public void EndCall(string caller) => Deliver(
        new BridgeLeaveEvent { EventType = "BridgeLeave", BridgeUniqueid = Bridge(caller), UniqueId = caller },
        new HangupEvent { EventType = "Hangup", UniqueId = caller, Cause = 16 });

    /// <summary>The next wrap-up event; a missing one fails the test at the bound.</summary>
    public async Task<CallWrapUpEvent> NextWrapUpAsync() =>
        await _pending.Reader.ReadAsync().AsTask().WaitAsync(Bound);

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        await _services.DisposeAsync();
        await _server.DisposeAsync();
    }

    void IObserver<SessionDomainEvent>.OnNext(SessionDomainEvent value)
    {
        if (value is not CallWrapUpEvent wrapUp)
        {
            return;
        }

        lock (_gate)
        {
            _wrapUps.Add(wrapUp);
        }

        _pending.Writer.TryWrite(wrapUp);
    }

    void IObserver<SessionDomainEvent>.OnError(Exception error)
    {
        // The manager's stream never faults; nothing to record.
    }

    void IObserver<SessionDomainEvent>.OnCompleted() => _pending.Writer.TryComplete();

    private static string CallerChannel(string caller) => $"PJSIP/trunk-{caller}";

    private static string MemberChannel(string memberLeg) => $"{Member}-{memberLeg}";

    private static string Bridge(string caller) => $"bridge-{caller}";

    private static NewChannelEvent NewChannel(
        string uniqueId, string channel, string state, string linkedId, string context, string? exten) => new()
        {
            EventType = "Newchannel", UniqueId = uniqueId, Channel = channel, ChannelState = state,
            CallerIdNum = "5551234", Context = context, Exten = exten, Priority = 1, Linkedid = linkedId,
            RawFields = new Dictionary<string, string>
            {
                ["Channel"] = channel, ["ChannelStateDesc"] = state, ["Uniqueid"] = uniqueId, ["Linkedid"] = linkedId,
                ["CallerIDNum"] = "5551234",
            },
        };

    private static async IAsyncEnumerable<ManagerEvent> NoEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    private void Deliver(params ManagerEvent[] frames)
    {
        foreach (var frame in frames)
        {
            _frames.OnNext(frame);
        }
    }
}
