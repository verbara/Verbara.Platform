using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Resolution;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Events;
using Verbara.Sdk.Pro.Cluster.Leadership;

// CA2012: NSubstitute records ValueTask-returning calls (GetVariableAsync,
// CreateAndConnectAsync) for `.Returns(...)` matching — the ValueTask is consumed
// by the extension, not awaited. This is a mock-setup artifact, not a real bug; the
// repo disables CA2012 in other mock-heavy test files for the same reason.
#pragma warning disable CA2012

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Phase 2.2 — unit coverage for <see cref="StasisInboundConsumer"/>, the
/// leader-gated ARI consumer that answers inbound Stasis calls and continues
/// them into the tenant's Asterisk queue.
///
/// <para>Two concerns are covered independently:</para>
/// <list type="number">
/// <item><b>Per-call routing</b> (<c>HandleStasisStartAsync</c>): tenant is read
/// from the <c>TENANT_ID</c> channel variable (the resolution contract — the
/// inbound trunk channel name is NOT tenant-prefixed), the DID is looked up in
/// <c>did_routes</c>, and the call is answered + continued to
/// <c>{tenant}-{queueName}</c> in the <c>stasis-queue</c> context. Every
/// unresolved step <b>fails closed</b> (Hangup, never a silent drop into
/// Stasis).</item>
/// <item><b>Leadership lifecycle</b> (<c>EvaluateLeadershipAsync</c>): only the
/// elected leader pod connects the ARI WebSocket (a physical call can't be
/// re-emitted, so this is stronger than the relay's short-circuit); losing
/// leadership tears the connection down.</item>
/// </list>
/// </summary>
public sealed class StasisInboundConsumerTests
{
    private const string Tenant = "tenant-x";
    private const string Channel = "1234567.0";
    private const string Did = "+15551234000";

    private static IConfiguration AriConfig(bool configured) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(configured
                ? new Dictionary<string, string?>
                {
                    ["Asterisk:Ari:BaseUrl"] = "http://asterisk:8088",
                    ["Asterisk:Ari:Username"] = "verbara",
                    ["Asterisk:Ari:Password"] = "secret",
                    ["Asterisk:Ari:Application"] = "verbara",
                }
                : new Dictionary<string, string?>())
            .Build();

    private static StasisInboundConsumer NewConsumer(
        IClusterLeader leader,
        IDidRouteStore didRoutes,
        IQueueStore queues,
        IAriClientFactory? factory = null,
        IReasonHintResolver? reasonHints = null,
        bool configured = true) =>
        new(
            factory ?? Substitute.For<IAriClientFactory>(),
            leader,
            didRoutes,
            queues,
            reasonHints ?? NoHintResolver(),
            Substitute.For<IServiceHeartbeat>(),
            AriConfig(configured),
            NullLogger<StasisInboundConsumer>.Instance);

    // Default resolver: no reason hint matches any scope (the implicit-capture path is a no-op).
    private static IReasonHintResolver NoHintResolver()
    {
        var resolver = Substitute.For<IReasonHintResolver>();
        resolver.ResolveAsync(Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<EntityId?>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
            .Returns((ReasonHint?)null);
        return resolver;
    }

    private static ReasonHint Hint(string reasonPath) => new()
    {
        HintId = EntityId.New(),
        TenantId = new TenantId(Tenant),
        Scope = ReasonHintScope.Did,
        ScopeRef = Did,
        ReasonPath = reasonPath,
    };

    private static (IAriClient client, IAriChannelsResource channels) NewClient()
    {
        var channels = Substitute.For<IAriChannelsResource>();
        var client = Substitute.For<IAriClient>();
        client.Channels.Returns(channels);
        return (client, channels);
    }

    private static StasisStartEvent InboundStart(string channelId = Channel, string did = Did) => new()
    {
        Type = "StasisStart",
        Application = "verbara",
        Args = ["inbound", did],
        Channel = new AriChannel { Id = channelId, Name = $"PJSIP/t-42-00001a" },
    };

    private static DidRoute Route(string queueId, bool isActive = true) => new()
    {
        RouteId = EntityId.New(),
        TenantId = new TenantId(Tenant),
        Did = Did,
        QueueId = EntityId.From(queueId),
        IsActive = isActive,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Queue Queue(string queueId, string name) => new()
    {
        QueueId = EntityId.From(queueId),
        TenantId = new TenantId(Tenant),
        Name = name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // ─── Per-call routing ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleStasisStartAsync_ShouldAnswerAndContinueToQueue_WhenTenantDidQueueAllResolve()
    {
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns(new AriVariable { Value = Tenant });
        var didRoutes = Substitute.For<IDidRouteStore>();
        didRoutes.GetByDidAsync(new TenantId(Tenant), Did, Arg.Any<CancellationToken>())
            .Returns(Route("q-support"));
        var queues = Substitute.For<IQueueStore>();
        queues.GetByIdAsync(new TenantId(Tenant), EntityId.From("q-support"), Arg.Any<CancellationToken>())
            .Returns(Queue("q-support", "Support"));

        var consumer = NewConsumer(LeaderStub(true), didRoutes, queues);
        await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await channels.Received(1).AnswerAsync(Channel, Arg.Any<CancellationToken>());
        await channels.Received(1).SetVariableAsync(Channel, "QUEUE_NAME", $"{Tenant}-Support", Arg.Any<CancellationToken>());
        await channels.Received(1).ContinueAsync(
            Channel, "stasis-queue", "s", 1, null, Arg.Any<CancellationToken>());
        await channels.DidNotReceive().HangupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStasisStartAsync_ShouldPassExactQueueNameViaVariable_WhenQueueNameContainsHyphensAndSpaces()
    {
        // The dialplan continues to a fixed extension "s" and reads Queue(${QUEUE_NAME}),
        // so a hyphenated / spaced / mixed-case queue name routes unchanged — no pattern to
        // miss, no post-Answer silent drop (the HIGH review finding's fix).
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns(new AriVariable { Value = Tenant });
        var didRoutes = Substitute.For<IDidRouteStore>();
        didRoutes.GetByDidAsync(new TenantId(Tenant), Did, Arg.Any<CancellationToken>())
            .Returns(Route("q-vn"));
        var queues = Substitute.For<IQueueStore>();
        queues.GetByIdAsync(new TenantId(Tenant), EntityId.From("q-vn"), Arg.Any<CancellationToken>())
            .Returns(Queue("q-vn", "Ventas Norte-2"));

        var consumer = NewConsumer(LeaderStub(true), didRoutes, queues);
        await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await channels.Received(1).SetVariableAsync(
            Channel, "QUEUE_NAME", $"{Tenant}-Ventas Norte-2", Arg.Any<CancellationToken>());
        await channels.Received(1).ContinueAsync(
            Channel, "stasis-queue", "s", 1, null, Arg.Any<CancellationToken>());
        await channels.DidNotReceive().HangupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Typification P1 implicit voice capture (VERBARA_REASON channel var) ─────

    [Fact]
    public async Task HandleInbound_ShouldSetReasonChannelVar_WhenHintResolves()
    {
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns(new AriVariable { Value = Tenant });
        var didRoutes = Substitute.For<IDidRouteStore>();
        var route = Route("q-support");
        didRoutes.GetByDidAsync(new TenantId(Tenant), Did, Arg.Any<CancellationToken>())
            .Returns(route);
        var queues = Substitute.For<IQueueStore>();
        queues.GetByIdAsync(new TenantId(Tenant), EntityId.From("q-support"), Arg.Any<CancellationToken>())
            .Returns(Queue("q-support", "Support"));
        var reasonHints = Substitute.For<IReasonHintResolver>();
        const string ReasonPath = "[\"CITAS\",\"REPROG\"]";
        reasonHints.ResolveAsync(
                new TenantId(Tenant), Did, route.QueueId, ChannelType.Voice, Arg.Any<CancellationToken>())
            .Returns(Hint(ReasonPath));

        var consumer = NewConsumer(LeaderStub(true), didRoutes, queues, reasonHints: reasonHints);
        await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        // The reason taxonomy path rides on the VERBARA_REASON channel var alongside QUEUE_NAME,
        // and the call still continues into the queue.
        await channels.Received(1).SetVariableAsync(Channel, "VERBARA_REASON", ReasonPath, Arg.Any<CancellationToken>());
        await channels.Received(1).SetVariableAsync(Channel, "QUEUE_NAME", $"{Tenant}-Support", Arg.Any<CancellationToken>());
        await channels.Received(1).ContinueAsync(
            Channel, "stasis-queue", "s", 1, null, Arg.Any<CancellationToken>());
        await channels.DidNotReceive().HangupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInbound_ShouldNotSetReasonChannelVar_WhenNoHint()
    {
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns(new AriVariable { Value = Tenant });
        var didRoutes = Substitute.For<IDidRouteStore>();
        didRoutes.GetByDidAsync(new TenantId(Tenant), Did, Arg.Any<CancellationToken>())
            .Returns(Route("q-support"));
        var queues = Substitute.For<IQueueStore>();
        queues.GetByIdAsync(new TenantId(Tenant), EntityId.From("q-support"), Arg.Any<CancellationToken>())
            .Returns(Queue("q-support", "Support"));

        // Default resolver returns null (no hint) — the VERBARA_REASON var must never be set, and the
        // call routes into the queue unchanged (voice routing never depends on a typification hint).
        var consumer = NewConsumer(LeaderStub(true), didRoutes, queues);
        await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await channels.DidNotReceive().SetVariableAsync(Channel, "VERBARA_REASON", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await channels.Received(1).SetVariableAsync(Channel, "QUEUE_NAME", $"{Tenant}-Support", Arg.Any<CancellationToken>());
        await channels.Received(1).ContinueAsync(
            Channel, "stasis-queue", "s", 1, null, Arg.Any<CancellationToken>());
        await channels.DidNotReceive().HangupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStasisStartAsync_ShouldHangupAndNotContinue_WhenTenantVarMissing()
    {
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns(new AriVariable { Value = "" });

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>());
        await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await channels.Received(1).HangupAsync(Channel, Arg.Any<CancellationToken>());
        await channels.DidNotReceive().AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await channels.DidNotReceive().ContinueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStasisStartAsync_ShouldHangup_WhenTenantVarReadThrows()
    {
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns<AriVariable>(_ => throw new InvalidOperationException("variable not set (404)"));

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>());
        await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await channels.Received(1).HangupAsync(Channel, Arg.Any<CancellationToken>());
        await channels.DidNotReceive().AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStasisStartAsync_ShouldHangup_WhenNoDidRoute()
    {
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns(new AriVariable { Value = Tenant });
        var didRoutes = Substitute.For<IDidRouteStore>();
        didRoutes.GetByDidAsync(new TenantId(Tenant), Did, Arg.Any<CancellationToken>())
            .Returns((DidRoute?)null);

        var consumer = NewConsumer(LeaderStub(true), didRoutes, Substitute.For<IQueueStore>());
        await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await channels.Received(1).HangupAsync(Channel, Arg.Any<CancellationToken>());
        await channels.DidNotReceive().AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStasisStartAsync_ShouldHangup_WhenRouteInactive()
    {
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns(new AriVariable { Value = Tenant });
        var didRoutes = Substitute.For<IDidRouteStore>();
        didRoutes.GetByDidAsync(new TenantId(Tenant), Did, Arg.Any<CancellationToken>())
            .Returns(Route("q-support", isActive: false));

        var consumer = NewConsumer(LeaderStub(true), didRoutes, Substitute.For<IQueueStore>());
        await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await channels.Received(1).HangupAsync(Channel, Arg.Any<CancellationToken>());
        await channels.DidNotReceive().ContinueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStasisStartAsync_ShouldHangup_WhenQueueMissing()
    {
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns(new AriVariable { Value = Tenant });
        var didRoutes = Substitute.For<IDidRouteStore>();
        didRoutes.GetByDidAsync(new TenantId(Tenant), Did, Arg.Any<CancellationToken>())
            .Returns(Route("q-gone"));
        var queues = Substitute.For<IQueueStore>();
        queues.GetByIdAsync(new TenantId(Tenant), EntityId.From("q-gone"), Arg.Any<CancellationToken>())
            .Returns((Queue?)null);

        var consumer = NewConsumer(LeaderStub(true), didRoutes, queues);
        await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await channels.Received(1).HangupAsync(Channel, Arg.Any<CancellationToken>());
        await channels.DidNotReceive().ContinueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStasisStartAsync_ShouldHangupAndNotThrow_WhenAnswerFails()
    {
        var (client, channels) = NewClient();
        channels.GetVariableAsync(Channel, "TENANT_ID", Arg.Any<CancellationToken>())
            .Returns(new AriVariable { Value = Tenant });
        var didRoutes = Substitute.For<IDidRouteStore>();
        didRoutes.GetByDidAsync(new TenantId(Tenant), Did, Arg.Any<CancellationToken>())
            .Returns(Route("q-support"));
        var queues = Substitute.For<IQueueStore>();
        queues.GetByIdAsync(new TenantId(Tenant), EntityId.From("q-support"), Arg.Any<CancellationToken>())
            .Returns(Queue("q-support", "Support"));
        channels.When(c => c.AnswerAsync(Channel, Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("ARI 503"));

        var consumer = NewConsumer(LeaderStub(true), didRoutes, queues);
        var act = async () => await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        await channels.Received(1).HangupAsync(Channel, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStasisStartAsync_ShouldIgnore_WhenChannelIdMissing()
    {
        var (client, channels) = NewClient();
        var evt = InboundStart();
        evt.Channel = null;

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>());
        await consumer.HandleStasisStartAsync(client, evt, CancellationToken.None);

        await channels.DidNotReceive().GetVariableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await channels.DidNotReceive().HangupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStasisStartAsync_ShouldIgnore_WhenChannelIdEmptyString()
    {
        // AriChannel.Id defaults to string.Empty — a partial/malformed StasisStart with a
        // present Channel but empty Id takes the same early-return path (can't act without an id).
        var (client, channels) = NewClient();
        var evt = InboundStart();
        evt.Channel = new AriChannel { Id = "" };

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>());
        await consumer.HandleStasisStartAsync(client, evt, CancellationToken.None);

        await channels.DidNotReceive().GetVariableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await channels.DidNotReceive().AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Stasis-event filter ────────────────────────────────────────────────────

    [Theory]
    [InlineData("inbound", true)]
    [InlineData("internal", false)]
    [InlineData("", false)]
    public void IsInboundStart_ShouldMatchOnlyInboundPhase(string phase, bool expected)
    {
        var evt = new StasisStartEvent { Args = [phase, Did], Channel = new AriChannel { Id = Channel } };
        StasisInboundConsumer.IsInboundStart(evt).Should().Be(expected);
    }

    [Fact]
    public void IsInboundStart_ShouldReturnFalse_ForNonStasisStartEvent()
    {
        StasisInboundConsumer.IsInboundStart(new AriEvent { Type = "ChannelHangupRequest" }).Should().BeFalse();
    }

    [Fact]
    public void IsInboundStart_ShouldReturnFalse_WhenArgsEmpty()
    {
        StasisInboundConsumer.IsInboundStart(new StasisStartEvent { Args = [] }).Should().BeFalse();
    }

    // ─── Leadership lifecycle ───────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldConnectAndSubscribe_WhenBecomesLeader()
    {
        var (client, _) = NewClient();
        var factory = Substitute.For<IAriClientFactory>();
        factory.CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(client);

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);

        await factory.Received(1).CreateAndConnectAsync(
            Arg.Is<AriClientOptions>(o => o.BaseUrl == "http://asterisk:8088" && o.Application == "verbara"),
            Arg.Any<CancellationToken>());
        client.Received(1).Subscribe(Arg.Any<IObserver<AriEvent>>());
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldConnectOnlyOnce_WhenStillLeaderOnNextTick()
    {
        var (client, _) = NewClient();
        var factory = Substitute.For<IAriClientFactory>();
        factory.CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(client);

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);

        await factory.Received(1).CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldDisconnectAndDispose_WhenLosesLeadership()
    {
        var (client, _) = NewClient();
        var factory = Substitute.For<IAriClientFactory>();
        factory.CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(client);
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(true);

        var consumer = NewConsumer(
            leader, Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // connect

        leader.IsLeader.Returns(false);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // teardown

        await client.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await client.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_ShouldDisposeConnectedClient_WhenSubscribeThrowsAfterConnect()
    {
        // HIGH review finding: if Subscribe throws AFTER CreateAndConnectAsync returned a
        // live (connected) client, the client must still be torn down — never orphaned as an
        // open ARI WebSocket that the next tick would reconnect alongside (socket leak + churn).
        var (client, _) = NewClient();
        client.Subscribe(Arg.Any<IObserver<AriEvent>>())
            .Returns(_ => throw new InvalidOperationException("event subject already disposed"));
        var factory = Substitute.For<IAriClientFactory>();
        factory.CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(client);

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // connect → Subscribe throws

        await client.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await client.Received(1).DisposeAsync();

        // _client was reset → the next leader tick reconnects (no orphan held open).
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);
        await factory.Received(2).CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldTearDownAndReconnect_WhenConnectionFaults()
    {
        // Resilience: a dropped ARI WebSocket (observer OnError/OnCompleted → SignalConnectionFault)
        // must not leave the leader connected-but-deaf — the next tick re-establishes it.
        var (client, _) = NewClient();
        var factory = Substitute.For<IAriClientFactory>();
        factory.CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(client);

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // connect

        consumer.SignalConnectionFault();                              // WebSocket dropped
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // teardown + reconnect

        await client.Received(1).DisposeAsync();
        await factory.Received(2).CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldNotConnect_WhenNotLeader()
    {
        var factory = Substitute.For<IAriClientFactory>();

        var consumer = NewConsumer(
            LeaderStub(false), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);

        await factory.DidNotReceive().CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsAriConfigured_ShouldBeFalse_WhenBaseUrlMissing()
    {
        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(),
            configured: false);
        consumer.IsAriConfigured.Should().BeFalse();
    }

    private static IClusterLeader LeaderStub(bool isLeader)
    {
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isLeader);
        leader.Resource.Returns(VoiceLeaderResources.Inbound);
        return leader;
    }
}
