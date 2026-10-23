using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
using Verbara.Sdk.Enums;
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
        CreatedAt = DateTimeOffset.UtcNow,
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
    public async Task HandleInbound_ShouldStillRoute_WhenReasonHintResolverThrows()
    {
        // The reason-hint capture is OPTIONAL best-effort: a transient store/ARI fault during
        // resolution must NEVER hang up an otherwise-routable caller. The guard around
        // ResolveAsync + SetVariableAsync(VERBARA_REASON) swallows the failure and the call
        // still answers + continues into the queue.
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
        reasonHints.ResolveAsync(
                new TenantId(Tenant), Did, route.QueueId, ChannelType.Voice, Arg.Any<CancellationToken>())
            .Returns<ReasonHint?>(_ => throw new InvalidOperationException("transient store failure"));

        var consumer = NewConsumer(LeaderStub(true), didRoutes, queues, reasonHints: reasonHints);
        var act = async () => await consumer.HandleStasisStartAsync(client, InboundStart(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        // VERBARA_REASON never stamped (the resolve threw), but the call routes anyway.
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
    // stasis-app-has-one-registrant (Platform/ADR-0044): every connection is IAriClientFactory.Create →
    // Subscribe → ConnectAsync, so an event Asterisk delivers while the client is still connecting reaches the
    // consumer (Sdk 2.8.0 starts its pump inside ConnectAsync and its subject does not replay).

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldConnectAndSubscribe_WhenBecomesLeader()
    {
        var (client, _) = NewClient();
        var factory = Substitute.For<IAriClientFactory>();
        factory.Create(Arg.Any<AriClientOptions>()).Returns(client);

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);

        factory.Received(1).Create(
            Arg.Is<AriClientOptions>(o => o != null && o.BaseUrl == "http://asterisk:8088" && o.Application == "verbara"));
        Received.InOrder(() =>
        {
            client.Subscribe(Arg.Any<IObserver<AriEvent>>());
            _ = client.ConnectAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldConnectOnlyOnce_WhenStillLeaderOnNextTick()
    {
        var (client, _) = NewClient();
        var factory = Substitute.For<IAriClientFactory>();
        factory.Create(Arg.Any<AriClientOptions>()).Returns(client);

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);

        factory.Received(1).Create(Arg.Any<AriClientOptions>());
        await client.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldDisconnectAndDispose_WhenLosesLeadership()
    {
        var (client, _) = NewClient();
        var factory = Substitute.For<IAriClientFactory>();
        factory.Create(Arg.Any<AriClientOptions>()).Returns(client);
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(true);

        var consumer = NewConsumer(
            leader, Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // connect

        leader.IsLeader.Returns(false);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // teardown
        await consumer.PendingRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await client.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await client.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await client.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_ShouldDisposeTheClient_WhenSubscribeThrowsBeforeConnect()
    {
        // HIGH review finding, kept under the new order: a client whose Subscribe throws must still be released —
        // never orphaned (socket leak + churn) — and the next leader tick connects again.
        var (client, _) = NewClient();
        client.Subscribe(Arg.Any<IObserver<AriEvent>>())
            .Returns(_ => throw new InvalidOperationException("event subject already disposed"));
        var factory = Substitute.For<IAriClientFactory>();
        factory.Create(Arg.Any<AriClientOptions>()).Returns(client);

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // Create → Subscribe throws

        await client.DidNotReceive().ConnectAsync(Arg.Any<CancellationToken>());
        await client.Received(1).DisposeAsync();

        // No connection was published → the next leader tick creates a new client (no orphan held open).
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);
        factory.Received(2).Create(Arg.Any<AriClientOptions>());
    }

    [Fact]
    public async Task ConnectAsync_ShouldDisposeTheClient_WhenConnectThrows()
    {
        var (client, _) = NewClient();
        client.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(new HttpRequestException("connection refused")));
        var factory = Substitute.For<IAriClientFactory>();
        factory.Create(Arg.Any<AriClientOptions>()).Returns(client);

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);

        await client.Received(1).DisposeAsync();
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);
        factory.Received(2).Create(Arg.Any<AriClientOptions>());
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldTearDownAndReconnect_WhenConnectionFaults()
    {
        // Resilience: a dropped ARI WebSocket (observer OnError/OnCompleted → SignalConnectionFault)
        // must not leave the leader connected-but-deaf — the next tick re-establishes it.
        var (first, _) = NewClient();
        var (second, _) = NewClient();
        var factory = Substitute.For<IAriClientFactory>();
        factory.Create(Arg.Any<AriClientOptions>()).Returns(first, second);

        var consumer = NewConsumer(
            LeaderStub(true), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // connect

        consumer.SignalConnectionFault();                              // WebSocket dropped
        await consumer.EvaluateLeadershipAsync(CancellationToken.None); // reconnect, then retire the old one
        await consumer.PendingRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await first.Received(1).DisposeAsync();
        factory.Received(2).Create(Arg.Any<AriClientOptions>());
        await second.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldNotConnect_WhenNotLeader()
    {
        // Follower guard: a follower opens no ARI connection by any factory path, so it neither registers the app
        // nor reacts to replacements (spec "A follower ignores replacements").
        var factory = Substitute.For<IAriClientFactory>();

        var consumer = NewConsumer(
            LeaderStub(false), Substitute.For<IDidRouteStore>(), Substitute.For<IQueueStore>(), factory);
        await consumer.EvaluateLeadershipAsync(CancellationToken.None);

        factory.DidNotReceive().Create(Arg.Any<AriClientOptions>());
        await factory.DidNotReceive().CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>());
    }

    // ─── Replaced registration (ApplicationReplaced), backoff, cap, make-before-break ─────────────────
    // stasis-app-has-one-registrant D4-D7. Each client the substitute factory hands out records the observer
    // it was subscribed with; ConnectAsync can deliver events to that observer before it returns.

    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static ApplicationReplacedEvent Replaced(string application = "verbara") =>
        new() { Type = "ApplicationReplaced", Application = application };

    /// <summary>
    /// A leader consumer over a substitute factory whose clients are recorded in order, on a
    /// <see cref="FakeTimeProvider"/>, with a capturing logger and its own <see cref="IMeterFactory"/>.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _metrics;
        private readonly MeterListener _listener = new();
        private long _replacedCount;

        public Rig(bool routable = false)
        {
            Leader.IsLeader.Returns(true);
            Leader.Resource.Returns(VoiceLeaderResources.Inbound);
            Factory.Create(Arg.Any<AriClientOptions>()).Returns(_ => Track());
            // Also answer the old factory path so a test fails on behaviour, not on a null client.
            Factory.CreateAndConnectAsync(Arg.Any<AriClientOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => new ValueTask<IAriClient>(Track()));

            _metrics = new ServiceCollection().AddMetrics().BuildServiceProvider();
            MeterFactory = _metrics.GetRequiredService<IMeterFactory>();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, MeterFactory)
                    && instrument.Meter.Name == "Verbara.Platform.Voice.Stasis"
                    && instrument.Name == "voice.stasis.app_replaced")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _replacedCount, value));
            _listener.Start();

            if (routable)
            {
                DidRoutes.GetByDidAsync(new TenantId(Tenant), Did, Arg.Any<CancellationToken>())
                    .Returns(Route("q-support"));
                Queues.GetByIdAsync(new TenantId(Tenant), EntityId.From("q-support"), Arg.Any<CancellationToken>())
                    .Returns(Queue("q-support", "Support"));
            }

            Consumer = new StasisInboundConsumer(
                Factory, Leader, DidRoutes, Queues, NoHintResolver(), Substitute.For<IServiceHeartbeat>(),
                AriConfig(configured: true), Logger, Time, MeterFactory);
        }

        public FakeTimeProvider Time { get; } = new(T0);
        public IAriClientFactory Factory { get; } = Substitute.For<IAriClientFactory>();
        public IClusterLeader Leader { get; } = Substitute.For<IClusterLeader>();
        public IDidRouteStore DidRoutes { get; } = Substitute.For<IDidRouteStore>();
        public IQueueStore Queues { get; } = Substitute.For<IQueueStore>();
        public CapturingLogger Logger { get; } = new();
        public IMeterFactory MeterFactory { get; }
        public StasisInboundConsumer Consumer { get; }

        public List<IAriClient> Clients { get; } = [];
        public List<IAriChannelsResource> Channels { get; } = [];
        public Dictionary<int, IObserver<AriEvent>> Observers { get; } = [];

        /// <summary>Runs inside client <c>i</c>'s <c>ConnectAsync</c>, before it returns.</summary>
        public Action<int>? DuringConnect { get; set; }

        /// <summary>When set, a routable call's <c>ContinueAsync</c> awaits this task (a handler in flight).</summary>
        public Task? HoldContinue { get; set; }

        public long ReplacedCount => Interlocked.Read(ref _replacedCount);

        public IObserver<AriEvent> Observer(int i) => Observers[i];

        public async Task TickAsync() => await Consumer.EvaluateLeadershipAsync(CancellationToken.None);

        public async Task AdvanceAndTickAsync(TimeSpan by)
        {
            Time.Advance(by);
            await TickAsync();
        }

        private IAriClient Track()
        {
            var i = Clients.Count;
            var (client, channels) = NewClient();
            channels.GetVariableAsync(Arg.Any<string>(), "TENANT_ID", Arg.Any<CancellationToken>())
                .Returns(new AriVariable { Value = Tenant });
            channels.ContinueAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => HoldContinue is { } held ? new ValueTask(held) : ValueTask.CompletedTask);
            client.Subscribe(Arg.Any<IObserver<AriEvent>>()).Returns(ci =>
            {
                Observers[i] = ci.Arg<IObserver<AriEvent>>();
                return Substitute.For<IDisposable>();
            });
            client.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                DuringConnect?.Invoke(i);
                return ValueTask.CompletedTask;
            });
            Clients.Add(client);
            Channels.Add(channels);
            return client;
        }

        public void Dispose()
        {
            _listener.Dispose();
            _metrics.Dispose();
            Consumer.Dispose();
        }
    }

    internal sealed class CapturingLogger : ILogger<StasisInboundConsumer>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public int Count(LogLevel level) => Entries.Count(e => e.Level == level);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>Polls <paramref name="condition"/> for up to 10 s (fire-and-forget handlers finish off the test thread).</summary>
    private static async Task EventuallyAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"Timed out waiting: {because}");
            await Task.Delay(10); // fence-allow: LOOP-DRIVER — poll pacing for a fire-and-forget handler, bounded by the 10-s deadline
        }
    }

    private static bool ReceivedCall(object substitute, string method) =>
        substitute.ReceivedCalls().Any(c => c.GetMethodInfo().Name == method);

    [Fact]
    public async Task Connect_ShouldHandleAStasisStartDeliveredBeforeConnectReturns()
    {
        using var rig = new Rig(routable: true);
        rig.DuringConnect = i => rig.Observer(i).OnNext(InboundStart());

        await rig.TickAsync();

        await EventuallyAsync(() => ReceivedCall(rig.Channels[0], "ContinueAsync"),
            "the StasisStart delivered inside ConnectAsync is routed on the client it arrived on");
        await rig.Channels[0].Received(1).ContinueAsync(
            Channel, "stasis-queue", "s", 1, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Connect_ShouldActOnAReplacementDeliveredBeforeConnectReturns()
    {
        using var rig = new Rig();
        rig.DuringConnect = i =>
        {
            if (i == 0)
                rig.Observer(0).OnNext(Replaced());
        };

        await rig.TickAsync();                                  // connect; the app is taken while connecting
        rig.ReplacedCount.Should().Be(1, "a replacement delivered while connecting is counted once the connection is live");

        await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(1)); // first backoff delay
        rig.Factory.Received(2).Create(Arg.Any<AriClientOptions>());
        await rig.Clients[1].Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldReRegister_WhenTheAppIsReplaced()
    {
        using var rig = new Rig();
        await rig.TickAsync();

        rig.Observer(0).OnNext(Replaced());
        await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));

        rig.Factory.Received(2).Create(Arg.Is<AriClientOptions>(o => o.Application == "verbara"));
        await rig.Clients[1].Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        rig.Logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Stasis app 'verbara' was taken by another ARI client"));
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldWaitEachBackoffDelay_WhenReplacedRepeatedly()
    {
        using var rig = new Rig();
        await rig.TickAsync();

        int[] delays = [1, 2, 4, 8, 16];
        for (var k = 1; k <= delays.Length; k++)
        {
            var delay = delays[k - 1];
            var before = rig.Clients.Count;
            rig.Observer(before - 1).OnNext(Replaced());

            await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(delay - 1));
            rig.Clients.Should().HaveCount(before, $"replacement {k} waits {delay} s, so no connection at {delay - 1} s");

            await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
            rig.Clients.Should().HaveCount(before + 1, $"replacement {k} reconnects once its {delay}-s delay has passed");
        }
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldReconnectFiveTimesAndLogAnError_WhenEveryReconnectIsReplacedForTenMinutes()
    {
        using var rig = new Rig();
        await rig.TickAsync();
        await ReplaceEveryConnectionForTenMinutesAsync(rig);

        rig.Clients.Should().HaveCount(6, "the first connection plus five capped reconnects inside 10 minutes");
        rig.Logger.Count(LogLevel.Error).Should().Be(1);
        rig.Logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error && e.Message.Contains("re-registration capped")
            && e.Message.Contains("Asterisk:Ari:NodeApplication"));
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldReconnectAgain_WhenTheRollingWindowAdmitsIt()
    {
        using var rig = new Rig();
        await rig.TickAsync();
        await ReplaceEveryConnectionForTenMinutesAsync(rig);   // now T0 + 600 s; oldest reconnect at T0 + 1 s
        rig.Clients.Should().HaveCount(6);

        await rig.TickAsync();                                  // T0 + 600 s: the oldest reconnect is 599 s old
        rig.Clients.Should().HaveCount(6, "the window still holds five reconnects 1 s before the oldest ages out");

        await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(1)); // T0 + 601 s: it is 10 minutes old
        rig.Clients.Should().HaveCount(7, "the rolling window admits one reconnect once its oldest is 10 minutes old");
    }

    /// <summary>From T0 to T0 + 600 s, one tick a second; every connection is replaced as soon as it is live.</summary>
    private static async Task ReplaceEveryConnectionForTenMinutesAsync(Rig rig)
    {
        rig.Observer(0).OnNext(Replaced());
        var seen = rig.Clients.Count;
        for (var s = 1; s <= 600; s++)
        {
            await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
            if (rig.Clients.Count > seen)
            {
                seen = rig.Clients.Count;
                rig.Observer(seen - 1).OnNext(Replaced());
            }
        }
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldReplaceTheConnection_WhenTheClientReportsFaulted()
    {
        using var rig = new Rig();
        await rig.TickAsync();

        rig.Clients[0].State.Returns(AriConnectionState.Faulted);   // the Sdk's own reconnects ended
        await rig.TickAsync();
        await rig.Consumer.PendingRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));

        rig.Clients.Should().HaveCount(2);
        await rig.Clients[1].Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _ = rig.Clients[1].ConnectAsync(Arg.Any<CancellationToken>());
            _ = rig.Clients[0].DisconnectAsync(Arg.Any<CancellationToken>());
            _ = rig.Clients[0].DisposeAsync();
        });
    }

    [Fact]
    public async Task Reconnect_ShouldOpenTheNewClientBeforeClosingTheOld_WhenTheAppIsReplaced()
    {
        using var rig = new Rig();
        await rig.TickAsync();

        rig.Observer(0).OnNext(Replaced());
        await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
        await rig.Consumer.PendingRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));

        rig.Clients.Should().HaveCount(2);
        Received.InOrder(() =>
        {
            _ = rig.Clients[1].ConnectAsync(Arg.Any<CancellationToken>());
            _ = rig.Clients[0].DisconnectAsync(Arg.Any<CancellationToken>());
            _ = rig.Clients[0].DisposeAsync();
        });
    }

    [Fact]
    public async Task Reconnect_ShouldNotDisposeTheOldClient_WhileAHandlerIsInFlight_WhenTheAppIsReplaced()
    {
        using var rig = new Rig(routable: true);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.HoldContinue = held.Task;
        await rig.TickAsync();

        rig.Observer(0).OnNext(InboundStart());                 // handler parks inside ContinueAsync
        await EventuallyAsync(() => ReceivedCall(rig.Channels[0], "ContinueAsync"), "the handler reached ContinueAsync");
        rig.HoldContinue = null;

        rig.Observer(0).OnNext(Replaced());
        await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(1)); // make-before-break reconnect
        rig.Clients.Should().HaveCount(2);

        await rig.Clients[0].Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await rig.Clients[0].DidNotReceive().DisposeAsync();

        held.SetResult();
        await rig.Consumer.PendingRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Clients[0].Received(1).DisposeAsync();
        await rig.Channels[0].Received(1).ContinueAsync(
            Channel, "stasis-queue", "s", 1, null, Arg.Any<CancellationToken>());
        await rig.Channels[1].DidNotReceive().ContinueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldCloseTheSocketAtOnceButDisposeAfterTheHandler_WhenLeadershipIsLost()
    {
        using var rig = new Rig(routable: true);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.HoldContinue = held.Task;
        await rig.TickAsync();

        rig.Observer(0).OnNext(InboundStart());
        await EventuallyAsync(() => ReceivedCall(rig.Channels[0], "ContinueAsync"), "the handler reached ContinueAsync");

        rig.Leader.IsLeader.Returns(false);
        await rig.TickAsync();

        await rig.Clients[0].Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await rig.Clients[0].DidNotReceive().DisposeAsync();

        held.SetResult();
        await rig.Consumer.PendingRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Clients[0].Received(1).DisposeAsync();
    }

    [Fact]
    public async Task EvaluateLeadershipAsync_ShouldDisposeAfterTheThirtySecondDrainDeadline_WhenAHandlerNeverFinishes()
    {
        using var rig = new Rig(routable: true);
        rig.HoldContinue = new TaskCompletionSource().Task;    // never completes
        await rig.TickAsync();

        rig.Observer(0).OnNext(InboundStart());
        await EventuallyAsync(() => ReceivedCall(rig.Channels[0], "ContinueAsync"), "the handler reached ContinueAsync");

        rig.Leader.IsLeader.Returns(false);
        await rig.TickAsync();
        await rig.Clients[0].Received(1).DisconnectAsync(Arg.Any<CancellationToken>());

        rig.Time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50); // fence-allow: SETTLE — give a wrongly-fired drain timer's continuation time to dispose before the negative check
        await rig.Clients[0].DidNotReceive().DisposeAsync();

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Consumer.PendingRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await EventuallyAsync(() => ReceivedCall(rig.Clients[0], "DisposeAsync"), "the drain deadline disposes the client");
    }

    [Fact]
    public async Task ApplicationReplaced_ShouldIncrementTheReplacedCounter()
    {
        using var rig = new Rig();
        await rig.TickAsync();

        rig.Observer(0).OnNext(Replaced());

        rig.ReplacedCount.Should().Be(1);
    }

    [Fact]
    public async Task ApplicationReplaced_ShouldNotCount_WhenItArrivesOnTheConnectionBeingReplaced()
    {
        // Guard: the consumer's own new registration makes Asterisk send ApplicationReplaced to the previous
        // socket; that event is neither counted nor followed by another reconnect.
        using var rig = new Rig();
        await rig.TickAsync();
        rig.DuringConnect = i =>
        {
            if (i >= 1 && rig.Observers.TryGetValue(i - 1, out var previous))
                previous.OnNext(Replaced());
        };

        if (rig.Observers.TryGetValue(0, out var first))
            first.OnNext(Replaced());                           // a real replacement
        var countedAfterRealReplacement = rig.ReplacedCount;

        await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(1)); // reconnect; the old socket hears our own registration
        var clientsAfterReconnect = rig.Clients.Count;

        for (var s = 0; s < 60; s++)
            await rig.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));

        rig.ReplacedCount.Should().Be(countedAfterRealReplacement);
        rig.Clients.Should().HaveCount(clientsAfterReconnect);
    }

    [Fact]
    public async Task Retire_ShouldDisconnectBeforeTheDrainBegins()
    {
        // Guard: a retiring client is disconnected (which cancels the Sdk's own reconnect loop) before anything
        // waits on its handlers, so it can never register the app again while it drains.
        using var rig = new Rig(routable: true);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.HoldContinue = held.Task;
        await rig.TickAsync();

        rig.Observer(0).OnNext(InboundStart());
        await EventuallyAsync(() => ReceivedCall(rig.Channels[0], "ContinueAsync"), "the handler reached ContinueAsync");

        rig.Leader.IsLeader.Returns(false);
        await rig.TickAsync();
        await rig.Clients[0].Received(1).DisconnectAsync(Arg.Any<CancellationToken>());

        held.SetResult();
        await rig.Consumer.PendingRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await EventuallyAsync(() => ReceivedCall(rig.Clients[0], "DisposeAsync"), "the client is released");
        Received.InOrder(() =>
        {
            _ = rig.Clients[0].DisconnectAsync(Arg.Any<CancellationToken>());
            _ = rig.Clients[0].DisposeAsync();
        });
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
