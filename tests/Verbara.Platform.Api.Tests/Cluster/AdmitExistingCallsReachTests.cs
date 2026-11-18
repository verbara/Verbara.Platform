using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Pro.Cluster;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Xunit.Abstractions;

namespace Verbara.Platform.Api.Tests.Cluster;

/// <summary>
/// Sdk <c>2.10.1</c> <c>AdmitExistingCalls</c>, called by Pro <c>2.29.0-pro</c> after an attach: a call already live on
/// a server gets a session (metadata <c>origin</c> = <c>reload</c>) and a <c>CallStartedEvent</c>. Its effect on
/// Platform is measured, one arm per reach path (boot, add-node, takeover), on the real Api registration
/// (<see cref="AdmissionHostFactory"/>: every hosted service <c>Program.cs</c> registers, started in its resolved order,
/// over the real Sdk session manager and fake Asterisk nodes). Each expected value below is the observation a run of
/// that arm recorded; the test fails when the reach path changes what any session-event subscriber does.
/// </summary>
public sealed class AdmitExistingCallsReachTests(ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>How every arm's decorator runs the admission (<see cref="AdmissionMode.Disabled"/> is the mutation).</summary>
    private const AdmissionMode ArmMode = AdmissionMode.Forward;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>Every subscriber of the session-event stream the Api registers.</summary>
    private static readonly string[] Subscribers =
    [
        "ConversationStatePushBridge", "CallOutcomeObserver", "VoiceConversationBridge", "EventStoreSubscriber",
        "CallAnalyticsEngine", "AgentAssistEngine",
    ];

    private readonly AdmissionPostgres _postgres = new();

    // ─── boot ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Boot_ShouldAdmitTheLiveCallsBeforeTheBridgeSubscribes_WhenTheyAreLiveAtBoot()
    {
        var boot = await RunBootAsync(BootOrder.Plain);

        // Control (i): the resolved hosted-service order, every session-event subscriber's position in it.
        boot.Positions.Should().BeInAscendingOrder(p => p.Index);
        boot.Positions.Select(p => p.Name).Should().Equal(
            "ConversationStatePushBridge", "CallOutcomeObserver", "ClusterManager", "LeaderElectionService",
            "VoiceConversationBridge", "EventStoreSubscriber", "CallAnalyticsEngine", "AgentAssistEngine");

        // Control (ii): the admitted sessions exist before VoiceConversationBridge.StartAsync runs.
        boot.ReloadSessionsBeforeBridgeStart.Should().Be(5, "the boot admission is awaited before the bridge starts");
        boot.SessionsBeforeEdge.Should().Be(Lines(
            "inbound-trunk: dir=Inbound tenant=- meta=[origin=reload]",
            "outbound-linked: dir=Inbound tenant=- meta=[origin=reload]",
            "campaign: dir=Inbound tenant=- meta=[origin=reload]",
            "trunk-not-campaign: dir=Inbound tenant=- meta=[origin=reload]",
            "not-inbound: dir=Outbound tenant=- meta=[origin=reload]"), "no admitted session carries a tenant when the bridge starts");

        // Who had subscribed when the admission ran (CallOutcomeObserver is a thread-pool race: boot-a and boot-b).
        Phase(boot, "ConversationStatePushBridge").Should().Be(AdmissionPhase.Before);
        foreach (var later in new[] { "VoiceConversationBridge", "EventStoreSubscriber", "CallAnalyticsEngine", "AgentAssistEngine" })
            Phase(boot, later).Should().Be(AdmissionPhase.After, "{0} starts after the cluster manager's awaited start", later);

        // What each did with the five admitted starts.
        boot.Pushes.Should().Equal([.. Enumerable.Repeat("push started tenant=default-tenant", 5), "push ended tenant=default-tenant"],
            "the push bridge pushes each admitted start, and the end of the one call that ended, under its default tenant");
        foreach (var later in new[] { "VoiceConversationBridge", "EventStoreSubscriber", "CallAnalyticsEngine", "AgentAssistEngine" })
            boot.Recorder.DeliveredTo(later).Should().NotContain(d => d.StartsWith("CallStartedEvent", StringComparison.Ordinal),
                "{0} subscribed after the boot admission returned", later);
        boot.GetVarsBeforeEdge.Should().BeEmpty("nothing reads a channel variable for a boot start before the bridge starts");

        // Once the lease is held each tenant-less admitted session gets its tenant once, from its Conversation, else one
        // TENANT_ID read: by the voice bridge's first leader edge or the event store's acquisition pass, whichever runs
        // first (both follow the AmiOwner lease; which one stamps a session is a race, so only the sum is asserted).
        boot.SessionsAfterEdge.Should().Be(Lines(
            "inbound-trunk: dir=Inbound tenant=acme meta=[origin=reload]",
            "outbound-linked: dir=Inbound tenant=acme meta=[origin=reload]",
            "campaign: dir=Inbound tenant=acme meta=[origin=reload]",
            "trunk-not-campaign: dir=Inbound tenant=globex meta=[origin=reload]",
            "not-inbound: dir=Outbound tenant=acme meta=[origin=reload]"));
        boot.GetVarsAfterEdge.Order(StringComparer.Ordinal).Should().Equal(
            "campaign:TENANT_ID", "inbound-trunk:TENANT_ID", "not-inbound:TENANT_ID", "trunk-not-campaign:TENANT_ID");
        boot.Conversations.Should().Be(Lines(
            "inbound-trunk: no conversation",
            "outbound-linked: conversation tenant=acme state=Active owner=Agent meta=[]",
            "campaign: no conversation",
            "trunk-not-campaign: no conversation",
            "not-inbound: no conversation"), "no Conversation is created, linked again or changed at boot");

        // The end of one admitted call reaches every subscriber.
        foreach (var subscriber in Subscribers)
            boot.Recorder.DeliveredTo(subscriber).Should().Contain(d => d == $"CallEndedEvent:{boot.Calls[0].LinkedId}");
        boot.Stores.Should().Be(boot.ExpectedStores, "measured: what the stores hold after the end of one admitted call");
        boot.Recorder.Log.Should().NotContain(s => s.Subscriber.EndsWith("SessionTracker", StringComparison.Ordinal),
            "the Sdk agent and queue trackers are never constructed");
    }

    [Fact]
    public async Task Boot_ShouldGiveTheObserverEveryAdmittedStart_WhenItSubscribedBeforeTheAdmission()
    {
        var boot = await RunBootAsync(BootOrder.ObserverFirst);

        Phase(boot, "CallOutcomeObserver").Should().Be(AdmissionPhase.Before);
        boot.Recorder.DeliveredTo("CallOutcomeObserver").Count(d => d.StartsWith("CallStartedEvent", StringComparison.Ordinal))
            .Should().Be(5);
        boot.CallAttempts.Should().Be("0", "no admitted call is bound to the observer's originate registry");
    }

    [Fact]
    public async Task Boot_ShouldGiveTheObserverNoAdmittedStart_WhenItSubscribesAfterTheAdmission()
    {
        var boot = await RunBootAsync(BootOrder.ObserverDeferred);

        boot.Recorder.Of("CallOutcomeObserver")!.Deferred.Should().BeTrue();
        boot.Recorder.DeliveredTo("CallOutcomeObserver").Should().NotContain(d => d.StartsWith("CallStartedEvent", StringComparison.Ordinal));
        boot.CallAttempts.Should().Be("0");
    }

    /// <summary>
    /// Boot control (ii)'s failing variant: a discarded admission (what a <c>_ =</c> at the Pro call site produces), held
    /// on a gate the test owns. No admitted session exists when the bridge starts, deterministically; once the gate opens
    /// the walk admits them, so the walk itself was intact.
    /// </summary>
    [Fact]
    public async Task BootControl_ShouldFindNoAdmittedSessionBeforeTheBridgeStarts_WhenTheAdmissionIsDiscarded()
    {
        var boot = await RunBootAsync(BootOrder.Plain, AdmissionMode.Discard);

        boot.ReloadSessionsBeforeBridgeStart.Should().Be(0, "a discarded admission returns before any session exists");
        boot.ReloadSessionsAfterGate.Should().Be(5, "the gated walk still admits every live call");
    }

    // ─── add-node ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddNode_ShouldAdmitTheNodesLiveCalls_AndReadNoMoreVariablesThanALiveStart()
    {
        await using var factory = new AdmissionHostFactory(_postgres.ConnectionString);
        var sp = factory.Services;
        var manager = sp.GetRequiredService<RecordingSessionManager>();
        manager.Mode = ArmMode;
        var hosted = await HostedServices.StartAsync(sp, output, beforeStart: null);
        try
        {
            await WaitReadyAsync(sp, manager);
            var conversations = sp.GetRequiredService<IConversationStore>();
            var calls = AdmissionCalls.Table("2002", await SeedLinkedConversationAsync(conversations, "2002.2"));
            var node = factory.Asterisk.Node("node-2.admission.invalid");
            foreach (var call in calls)
                node.Hold(call);
            var primary = factory.Asterisk.Node(AdmissionHostFactory.PrimaryHost);

            // The admission is reached through the add-node endpoint, called over HTTP as a platform admin calls it.
            using (var client = await PlatformAdminClientAsync(factory))
            using (var body = new StringContent(
                """{"nodeId":"node-2","amiHostname":"node-2.admission.invalid","amiPort":5038,"amiUsername":"admission","amiPassword":"admission"}""",
                Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("/api/v1/management/cluster/nodes", body))
            {
                response.StatusCode.Should().Be(HttpStatusCode.Created,
                    "the endpoint added the node: {0}", await response.Content.ReadAsStringAsync());
            }
            await WaitAsync(() => calls.All(c => manager.Recorder.DeliveredTo("VoiceConversationBridge").Contains($"CallStartedEvent:{c.LinkedId}"))
                && primary.GetVars.Count >= 2 * calls.Count, "the bridge handled every admitted start");
            var admittedReads = primary.GetVars.ToList();

            // Control: the same leg kinds starting live through the same node.
            var controls = AdmissionCalls.Table("2102", await SeedLinkedConversationAsync(conversations, "2102.2"));
            foreach (var control in controls)
                node.Start(control);
            await WaitAsync(() => primary.GetVars.Count >= admittedReads.Count + 2 * controls.Count, "the bridge handled every live start");
            var liveReads = primary.GetVars.Skip(admittedReads.Count).ToList();

            output.WriteLine(Report(manager, calls, admittedReads, liveReads));

            manager.AdmissionLog.Should().Contain("admission entered: node-2 (Forward)");
            AdmissionReport.Sessions(manager, calls).Should().Be(Lines(
                "inbound-trunk: dir=Inbound tenant=- meta=[origin=reload]",
                "outbound-linked: dir=Inbound tenant=- meta=[origin=reload]",
                "campaign: dir=Inbound tenant=- meta=[origin=reload]",
                "trunk-not-campaign: dir=Inbound tenant=- meta=[origin=reload]",
                "not-inbound: dir=Outbound tenant=- meta=[origin=reload]"));
            foreach (var subscriber in Subscribers)
                foreach (var call in calls)
                    manager.Recorder.DeliveredTo(subscriber).Should().Contain($"CallStartedEvent:{call.LinkedId}",
                        "{0} had subscribed before the runtime admission", subscriber);
            (await AdmissionReport.ConversationsAsync(conversations, calls)).Should().Be(Lines(
                "inbound-trunk: no conversation",
                "outbound-linked: conversation tenant=acme state=Active owner=Agent meta=[]",
                "campaign: no conversation",
                "trunk-not-campaign: no conversation",
                "not-inbound: no conversation"), "no Conversation is created, linked again or changed by the admission");

            // Campaign-leg GetVar cost: no admitted call reads more, by variable, than the same leg starting live.
            node.GetVars.Should().BeEmpty("the bridge reads every variable through the primary server");
            foreach (var (admitted, live) in calls.Zip(controls))
                ReadsOf(admittedReads, admitted).Should().BeSubsetOf(ReadsOf(liveReads, live),
                    "an admitted {0} reads no variable its live start does not", admitted.Label);
            ReadsOf(admittedReads, calls[2]).Should().Equal("VERBARA_OUTBOUND_ID", "DIALER_CAMPAIGN_ID");

            (await StoresAsync(sp)).Should().Be(ExpectedRuntimeStores);
        }
        finally
        {
            await hosted.StopAsync();
        }
    }

    // ─── takeover ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Takeover_ShouldAdmitOnlyTheCallWhoseReconstructionWasRefused()
    {
        await using var factory = new AdmissionHostFactory(_postgres.ConnectionString);
        var sp = factory.Services;
        var manager = sp.GetRequiredService<RecordingSessionManager>();
        manager.Mode = ArmMode;
        var primary = factory.Asterisk.Node(AdmissionHostFactory.PrimaryHost);
        var holder = new LiveCall("primary-holder", "3003.9", "PJSIP/trunk-3003-9", "from-trunk", "500",
            new Dictionary<string, string> { ["TENANT_ID"] = AdmissionCalls.Tenant });
        primary.Hold(holder);
        var hosted = await HostedServices.StartAsync(sp, output, beforeStart: null);
        try
        {
            await WaitReadyAsync(sp, manager);
            var held = manager.ActiveSessions.Single(s => s.LinkedId == holder.LinkedId);
            var conversations = sp.GetRequiredService<IConversationStore>();
            var calls = AdmissionCalls.Table("3003", await SeedLinkedConversationAsync(conversations, "3003.2"));
            var node = factory.Asterisk.Node("node-3.admission.invalid");
            foreach (var call in calls)
                node.Hold(call);
            await _postgres.QueryAsync(
                "INSERT INTO cluster_instances (instance_id, expires_at) VALUES ('dead-peer', now() + interval '1 hour') RETURNING instance_id");
            await sp.GetRequiredService<ClusterManager>().AddNodeAsync("node-3", NodeOptions("node-3.admission.invalid"));

            // A peer owns node-3 and stops with its link dead. The snapshots it left: the inbound trunk call under the
            // session id the primary call holds (refused), the other trunk call under its own id (accepted).
            await _postgres.QueryAsync("UPDATE cluster_nodes SET owner_instance = 'dead-peer' WHERE node_id = 'node-3' RETURNING node_id");
            await _postgres.QueryAsync(
                "INSERT INTO cluster_session_snapshots (server_id, linked_id, session_id, state, direction, created_at) VALUES " +
                $"('node-3', '{calls[0].LinkedId}', '{held.SessionId}', 'Connected', 'Inbound', now()), " +
                $"('node-3', '{calls[3].LinkedId}', 'snapshotaccepted0000000000000004', 'Connected', 'Inbound', now()) " +
                "ON CONFLICT (server_id, linked_id) DO UPDATE SET session_id = EXCLUDED.session_id RETURNING linked_id");
            var marks = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            manager.Recorder.Delivering += (subscriber, evt) =>
            {
                if (subscriber == "VoiceConversationBridge" && evt is Verbara.Sdk.Sessions.CallStartedEvent
                    && manager.GetById(evt.SessionId) is { } call)
                    marks.TryAdd(call.LinkedId, primary.GetVars.Count);
            };
            node.KillCurrentConnection();
            var connects = node.Connects;
            var deliveredBefore = manager.Recorder.DeliveredTo("VoiceConversationBridge").Count;
            primary.ClearGetVars();
            await _postgres.QueryAsync(
                "UPDATE cluster_instances SET expires_at = now() - interval '1 hour' WHERE instance_id = 'dead-peer' RETURNING instance_id");

            // The admission is reached through the failover coordinator's takeover (its reconcile pass), never by the test.
            await WaitAsync(() => manager.AdmissionLog.Count(l => l == "admission returned: node-3") == 2,
                "the takeover ran its admission", TimeSpan.FromSeconds(60));
            await WaitAsync(() => manager.Recorder.DeliveredTo("VoiceConversationBridge").Skip(deliveredBefore)
                .Contains($"CallStartedEvent:{calls[0].LinkedId}") && primary.GetVars.Count >= 2, "the bridge handled the admitted start");
            var takeoverDeliveries = manager.Recorder.DeliveredTo("VoiceConversationBridge").Skip(deliveredBefore).ToList();
            var admittedReads = ReadsOf(primary.GetVars.Skip(marks[calls[0].LinkedId]), calls[0]);

            // Control: a call of the same leg kind starting live through the node the takeover attached.
            var control = new LiveCall("inbound-trunk-live", "3103.1", "PJSIP/trunk-3103-1", "from-trunk", "500",
                new Dictionary<string, string> { ["TENANT_ID"] = AdmissionCalls.Tenant });
            node.Start(control);
            await WaitAsync(() => marks.TryGetValue(control.LinkedId, out var mark)
                && ReadsOf(primary.GetVars.Skip(mark), control).Count >= admittedReads.Count,
                "the bridge handled the live control's start");
            var liveReads = ReadsOf(primary.GetVars.Skip(marks[control.LinkedId]), control);

            output.WriteLine(Report(manager, calls, admittedReads, liveReads));
            output.WriteLine("== takeover deliveries to the bridge: " + string.Join(",", takeoverDeliveries));

            node.Connects.Should().Be(connects + 1, "the takeover replaced the dead server with a fresh connection");
            manager.GetById(held.SessionId).Should().BeSameAs(held, "the refused snapshot left the holder's call as it was");
            AdmissionReport.Sessions(manager, calls).Should().Be(Lines(
                "inbound-trunk: dir=Inbound tenant=- meta=[origin=reload]",
                "outbound-linked: dir=Inbound tenant=- meta=[]",
                "campaign: dir=Inbound tenant=- meta=[]",
                "trunk-not-campaign: dir=Inbound tenant=- meta=[]",
                "not-inbound: dir=Outbound tenant=- meta=[]"),
                "only the call whose reconstructed registration was refused is admitted; the others are reconstructed");
            manager.GetById("snapshotaccepted0000000000000004")!.LinkedId.Should().Be(calls[3].LinkedId,
                "the accepted snapshot was registered under its own session id");
            takeoverDeliveries.Where(d => d.StartsWith("CallStartedEvent", StringComparison.Ordinal))
                .Should().Equal($"CallStartedEvent:{calls[0].LinkedId}");
            takeoverDeliveries.Count(d => d.StartsWith("CallReleasedEvent", StringComparison.Ordinal)).Should().Be(5,
                "the peer's mirror of each call is released with its dead server");
            admittedReads.Should().Equal("VERBARA_OUTBOUND_ID", "DIALER_CAMPAIGN_ID");
            admittedReads.Should().BeSubsetOf(liveReads, "the admitted call reads no variable its live start does not");
            (await AdmissionReport.ConversationsAsync(conversations, calls)).Should().Be(Lines(
                "inbound-trunk: no conversation",
                "outbound-linked: conversation tenant=acme state=Active owner=Agent meta=[]",
                "campaign: no conversation",
                "trunk-not-campaign: no conversation",
                "not-inbound: no conversation"));
            (await StoresAsync(sp)).Should().Be(ExpectedRuntimeStores);
        }
        finally
        {
            await hosted.StopAsync();
        }
    }

    // ─── the boot runner ─────────────────────────────────────────────────────

    internal enum BootOrder
    {
        /// <summary>The order a plain start produces.</summary>
        Plain,

        /// <summary>(boot-a) the cluster manager starts only after the observer's subscription is recorded.</summary>
        ObserverFirst,

        /// <summary>(boot-b) the observer's subscription is forwarded only once the admission returned.</summary>
        ObserverDeferred,
    }

    private sealed record Position(int Index, string Name);

    private sealed record BootObservation(
        IReadOnlyList<LiveCall> Calls,
        IReadOnlyList<Position> Positions,
        int ReloadSessionsBeforeBridgeStart,
        int ReloadSessionsAfterGate,
        string SessionsBeforeEdge,
        string SessionsAfterEdge,
        IReadOnlyList<string> GetVarsBeforeEdge,
        IReadOnlyList<string> GetVarsAfterEdge,
        string Conversations,
        IReadOnlyList<string> Pushes,
        RecordingEvents Recorder,
        string Stores,
        string ExpectedStores,
        string CallAttempts);

    private async Task<BootObservation> RunBootAsync(BootOrder order, AdmissionMode mode = ArmMode)
    {
        await using var factory = new AdmissionHostFactory(_postgres.ConnectionString);
        var sp = factory.Services;
        var manager = sp.GetRequiredService<RecordingSessionManager>();
        manager.Mode = mode;
        if (order == BootOrder.ObserverDeferred)
            manager.DeferUntilAdmitted = "CallOutcomeObserver";

        var conversations = sp.GetRequiredService<IConversationStore>();
        var calls = AdmissionCalls.Table("1001", await SeedLinkedConversationAsync(conversations, "1001.2"));
        var primary = factory.Asterisk.Node(AdmissionHostFactory.PrimaryHost);
        foreach (var call in calls)
            primary.Hold(call);
        var pushes = new ConcurrentQueue<PushEvent>();
        using var pushSubscription = sp.GetRequiredService<IPushEventBus>().AsObservable().Subscribe(pushes.Enqueue);

        int Reloaded() => manager.ActiveSessions.Count(s => s.Metadata.TryGetValue("origin", out var o) && o == "reload");
        var beforeBridge = -1;
        var sessionsBeforeEdge = "";
        var getVarsBeforeEdge = new List<string>();
        var hosted = await HostedServices.StartAsync(sp, output, async service =>
        {
            if (service is ClusterManager && order == BootOrder.ObserverFirst)
                await WaitAsync(() => manager.Recorder.HasSubscribed("CallOutcomeObserver"), "boot-a: the observer subscribed");
            if (service is VoiceConversationBridge)
            {
                // Before the bridge and the event store start, nothing that stamps a tenant or reads a variable runs.
                beforeBridge = Reloaded();
                sessionsBeforeEdge = AdmissionReport.Sessions(manager, calls);
                getVarsBeforeEdge = [.. primary.GetVars];
            }
        });
        try
        {
            var afterGate = -1;
            if (mode == AdmissionMode.Discard)
            {
                manager.DiscardGate.TrySetResult();
                await manager.Admitted.Task.WaitAsync(Bound);
                afterGate = Reloaded();
                return new BootObservation(calls, [], beforeBridge, afterGate, "", "", [], [], "", [], manager.Recorder, "", "", "");
            }

            var positions = hosted.Services
                .Select((s, i) => new Position(i, s.GetType().Name))
                .Where(p => Subscribers.Contains(p.Name) || p.Name is "ClusterManager" or "LeaderElectionService")
                .GroupBy(p => p.Name).Select(g => g.First()).OrderBy(p => p.Index).ToList();
            output.WriteLine("== subscriber positions: " + string.Join(", ", positions.Select(p => $"{p.Name}@{p.Index}")));

            await WaitReadyAsync(sp, manager);

            // The first leader edge, run by the test, and the bridge's own one-second leader poll, whichever comes first.
            await hosted.Services.OfType<VoiceConversationBridge>().Single().ObserveLeadershipAsync();
            await WaitAsync(() => manager.ActiveSessions.All(s => !string.IsNullOrEmpty(s.TenantId)), "the edge stamped every tenant");
            var sessionsAfterEdge = AdmissionReport.Sessions(manager, calls);
            var getVarsAfterEdge = primary.GetVars.Skip(getVarsBeforeEdge.Count).ToList();
            var conversationReport = await AdmissionReport.ConversationsAsync(conversations, calls);

            primary.End(calls[0]);
            await WaitAsync(() => Subscribers.All(s => manager.Recorder.DeliveredTo(s).Contains($"CallEndedEvent:{calls[0].LinkedId}")),
                "every subscriber received the end of the admitted call");
            var stores = await StoresAsync(sp);
            var attempts = (await _postgres.QueryAsync("SELECT count(*) FROM call_attempts")).Single();

            output.WriteLine("== admission log" + Environment.NewLine + string.Join(Environment.NewLine, manager.AdmissionLog));
            output.WriteLine("== subscriptions" + Environment.NewLine + AdmissionReport.Subscriptions(manager.Recorder));
            output.WriteLine("== before edge" + Environment.NewLine + sessionsBeforeEdge);
            output.WriteLine("== after edge" + Environment.NewLine + sessionsAfterEdge);
            output.WriteLine("== getvar after edge: " + string.Join(",", getVarsAfterEdge));
            output.WriteLine("== conversations" + Environment.NewLine + conversationReport);
            output.WriteLine("== pushes" + Environment.NewLine + AdmissionReport.Pushes(pushes));
            output.WriteLine("== deliveries" + Environment.NewLine + AdmissionReport.Deliveries(manager.Recorder, calls));
            output.WriteLine("== stores after the end of inbound-trunk" + Environment.NewLine + stores);
            output.WriteLine($"== call_attempts: {attempts}");

            return new BootObservation(calls, positions, beforeBridge, afterGate, sessionsBeforeEdge, sessionsAfterEdge,
                getVarsBeforeEdge, getVarsAfterEdge, conversationReport,
                [.. AdmissionReport.Pushes(pushes).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)],
                manager.Recorder, stores, ExpectedRuntimeStores, attempts);
        }
        finally
        {
            await hosted.StopAsync();
        }
    }

    // ─── shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Measured on every arm: what the event store, the analysis queue and Agent Assist hold once the subscribers handled
    /// the admitted calls (and, at boot, the end of one of them).
    /// </summary>
    private const string ExpectedRuntimeStores = "session_events=0 completed_sessions=0 call_analysis_queue=0 agent_assist_sessions=0";

    private async Task<string> StoresAsync(IServiceProvider sp)
    {
        var events = (await _postgres.QueryAsync("SELECT count(*) FROM session_events")).Single();
        var completed = (await _postgres.QueryAsync("SELECT count(*) FROM completed_sessions")).Single();
        var queue = (await _postgres.QueryAsync("SELECT count(*) FROM call_analysis_queue")).Single();
        var assist = sp.GetRequiredService<AgentAssistSupervisor>().ActiveSessions.Count;
        return string.Create(CultureInfo.InvariantCulture,
            $"session_events={events} completed_sessions={completed} call_analysis_queue={queue} agent_assist_sessions={assist}");
    }

    private static string Report(RecordingSessionManager manager, IReadOnlyList<LiveCall> calls,
        IReadOnlyList<string> admittedReads, IReadOnlyList<string> liveReads)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== admission log").AppendLine(string.Join(Environment.NewLine, manager.AdmissionLog));
        sb.AppendLine("== subscriptions").AppendLine(AdmissionReport.Subscriptions(manager.Recorder));
        sb.AppendLine("== sessions").Append(AdmissionReport.Sessions(manager, calls));
        sb.AppendLine("== deliveries").AppendLine(AdmissionReport.Deliveries(manager.Recorder, calls));
        sb.AppendLine("== GetVar by admitted calls: " + string.Join(",", admittedReads));
        sb.AppendLine("== GetVar by live controls: " + string.Join(",", liveReads));
        return sb.ToString();
    }

    /// <summary>The variables read for <paramref name="call"/>'s channel, in order.</summary>
    private static List<string> ReadsOf(IEnumerable<string> reads, LiveCall call) =>
        [.. reads.Where(r => r.StartsWith(call.Channel + ":", StringComparison.Ordinal)).Select(r => r[(call.Channel.Length + 1)..])];

    private static AdmissionPhase Phase(BootObservation boot, string subscriber) =>
        boot.Recorder.Of(subscriber)?.Phase ?? throw new InvalidOperationException($"{subscriber} never subscribed");

    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + Environment.NewLine));

    private static ClusterNodeOptions NodeOptions(string hostname) => new()
    {
        Ami = new AmiConnectionOptions { Hostname = hostname, Port = 5038, Username = "admission", Password = "admission" },
    };

    /// <summary>
    /// A client of the rig's own host that authenticates with a management API key of a platform tenant, the caller the
    /// <c>PlatformAdminOnly</c> policy admits.
    /// </summary>
    private static async Task<HttpClient> PlatformAdminClientAsync(AdmissionHostFactory factory)
    {
        const string tenant = "admission-platform";
        const string rawKey = "admission-mgmt-key";
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITenantStore>().UpsertAsync(new Tenant
            {
                TenantId = tenant,
                Name = "Admission platform",
                Status = TenantStatus.Active,
                Type = TenantType.Platform,
                ParentTenantId = null,
            });
            await scope.ServiceProvider.GetRequiredService<IApiKeyStore>().SaveAsync(new ApiKey
            {
                KeyId = EntityId.From("admission-mgmt-key-id"),
                TenantId = new TenantId(tenant),
                Name = "Admission management key",
                HashedKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))),
                Scopes = ["platform:*"],
                KeyType = ApiKeyType.Management,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {rawKey}");
        return client;
    }

    private static async Task<string> SeedLinkedConversationAsync(IConversationStore conversations, string linkedId)
    {
        var id = EntityId.New();
        await conversations.SaveAsync(new Conversation
        {
            ConversationId = id,
            TenantId = new TenantId(AdmissionCalls.Tenant),
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = ConversationState.Active,
            Owner = new ConversationOwner(ConversationOwnerKind.Agent, EntityId.New()),
            CreatedAt = DateTimeOffset.UtcNow,
            VoiceLinkedId = linkedId,
        }, CancellationToken.None);
        return id.Value;
    }

    /// <summary>
    /// The readiness wait: every session-event subscriber recorded by the recorder and the
    /// <c>AmiOwner</c> lease held, bounded; a timeout is a red with the recorder's log.
    /// </summary>
    private async Task WaitReadyAsync(IServiceProvider sp, RecordingSessionManager manager)
    {
        var leader = sp.GetRequiredKeyedService<IClusterLeader>(VoiceLeaderResources.AmiOwner);
        var logs = sp.GetRequiredService<RecordingLogs>();
        // The event store's writer is the AmiOwner lease holder: its first writer edge runs an acquisition pass that
        // resolves the tenant of every call it retained, so a runtime admission waits for that pass to have started.
        await WaitAsync(() => leader.IsLeader && Subscribers.All(manager.Recorder.HasSubscribed)
                && logs.Contains("EventStore acquisition pass started"),
            "readiness: every subscriber recorded, the AmiOwner lease held and the event store's acquisition pass started",
            onTimeout: () => AdmissionReport.Subscriptions(manager.Recorder) + Environment.NewLine + string.Join(Environment.NewLine, logs.Messages));
    }

    private async Task WaitAsync(Func<bool> condition, string what, TimeSpan? bound = null, Func<string>? onTimeout = null)
    {
        using var guard = new CancellationTokenSource(bound ?? Bound);
        while (!condition())
        {
            if (guard.IsCancellationRequested)
            {
                if (onTimeout is not null)
                    output.WriteLine(onTimeout());
                throw new TimeoutException($"bounded wait ran out: {what}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None); // fence-allow: LOOP-DRIVER — bounded poll on an observable condition (recorder, lease, fake AMI) that raises no signal of its own
        }
    }

    async Task IAsyncLifetime.InitializeAsync() => await _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.StopAsync();

    /// <summary>The hosted services <c>Program.cs</c> registers, started in their resolved registration order.</summary>
    private sealed class HostedServices(IReadOnlyList<IHostedService> services)
    {
        public IReadOnlyList<IHostedService> Services => services;

        public static async Task<HostedServices> StartAsync(IServiceProvider sp, ITestOutputHelper output,
            Func<IHostedService, Task>? beforeStart)
        {
            var services = sp.GetKeyedServices<IHostedService>(AdmissionHostFactory.HostedKey).ToList();
            output.WriteLine("== resolved hosted order: " + string.Join(",", services.Select((s, i) => $"{i}:{s.GetType().Name}")));
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach (var s in services.OfType<IHostedLifecycleService>())
                await s.StartingAsync(guard.Token);
            foreach (var s in services)
            {
                if (beforeStart is not null)
                    await beforeStart(s);
                await s.StartAsync(guard.Token);
            }

            foreach (var s in services.OfType<IHostedLifecycleService>())
                await s.StartedAsync(guard.Token);
            return new HostedServices(services);
        }

        public async Task StopAsync()
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var s in services.Reverse())
            {
                try
                {
                    await s.StopAsync(guard.Token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Stopping is cleanup: a service that throws on stop changes no observation.
                }
            }
        }
    }
}
