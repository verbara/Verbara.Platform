using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Cluster.Postgres.DependencyInjection;
using Verbara.Sdk.Pro.Cluster.DependencyInjection;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The Api's per-resource leader-election wiring (Platform/ADR-0022 Phase A.5), moved out of <c>Program.cs</c>
/// because of its LOC budget (<c>scripts/check-endpoint-invariants.py</c> gate #9). It holds three lease
/// categories: <b>always</b> (W3/W4/W5: a lease in the cluster, an <see cref="AlwaysLeader"/> stub on a single
/// node); <b>capability lease with capability-only consumers</b> (voice: a lease when the pod is capable, nothing
/// otherwise, because its consumers are registered under the same flag); and <b>capability lease with an
/// every-pod consumer</b> (<see cref="CampaignMetricsLeaderResources.Webhooks"/>: a lease when the pod runs the
/// metrics poller, an <see cref="AlwaysLeader"/> stub on every other pod in either branch, because
/// <see cref="WebhookDispatcher"/> is registered on every pod). The leader-gated workers themselves stay
/// registered in <c>Program.cs</c>.
/// </summary>
public static class ClusterLeadershipExtensions
{
    /// <summary>
    /// Registers the Postgres-lease-backed keyed <see cref="IClusterLeader"/>s of a clustered deployment.
    /// <para>
    /// Phase 2.2 voice inbound: <c>StasisInboundConsumer</c> opens an ARI WebSocket, and Asterisk delivers a
    /// Stasis app to exactly ONE socket, so only the elected leader pod connects and consumes (a physical call
    /// can't be re-emitted, so this gates the <i>connection</i>, stronger than the relay's per-event
    /// short-circuit). Mirrors the Realtime microservice wiring (ADR-0022 Phase A.5): a keyed <c>"Cluster"</c>
    /// <see cref="NpgsqlDataSource"/> (the SAME shared instance as the transport, no extra pool, ADR-0015) backs
    /// <c>AddPostgresDistributedLock</c>, and the builder <c>AddVerbaraCluster</c> overload registers the keyed
    /// <see cref="IClusterLeader"/>. This coexists with the <c>ClusterOptions</c> <c>AddVerbaraCluster</c> in
    /// <c>Program.cs</c> (independent overloads). Gated additionally on <c>Asterisk:Ari</c> being configured
    /// (<paramref name="voiceInboundEnabled"/>) so that ONLY an ARI-capable pod can ever win the voice-inbound
    /// lease, making "a pod that can't connect Asterisk never holds the lease while a capable pod stays
    /// follower" a structural invariant rather than relying on Helm env homogeneity.
    /// </para>
    /// <para>
    /// Phase 3B.0 adds a SECOND voice lease: <c>voice:ami:owner</c> gates the single pod that EMITS AMI-driven
    /// side-effects (<c>VoiceConversationBridge</c>: tracked voice Conversation, capacity, agent Busy/ACW, tenant
    /// stamping). Every pod's <c>CallSessionManager</c> observes the same broadcast AMI stream (warm standby),
    /// but only the leader acts, so side-effects happen exactly once cluster-wide with no cold-start gap on
    /// failover. It is gated on <c>Asterisk:Ami</c> (Hostname, <paramref name="voiceAmiEnabled"/>), NOT Ari,
    /// because AMI capability ≠ ARI capability; a pod must be AMI-capable to own the AMI side-effect plane. W5b
    /// (callback rescue) is voice-AMI only.
    /// </para>
    /// <para>
    /// W3: the cluster/lock/leader registration sits at the cluster-connection level (not in a voice-only
    /// sub-block) because the agent-liveness lease must run on EVERY clustered deployment, not just
    /// voice-capable ones; W4 and W5 likewise. The one shared lock backend and keyed <c>"Cluster"</c> datasource
    /// (one pool, ADR-0015) materialize each lease independently, through a single <c>AddVerbaraCluster</c> with
    /// one <c>RegisterLeader</c> call per lease: W3/W4/W5 ALWAYS, the voice leases only when their respective
    /// Asterisk capability is configured.
    /// </para>
    /// <para>
    /// Campaign-metrics webhooks (dialer-sees-every-tenants-campaigns D5): every replica polls and publishes
    /// campaign metrics to its own SSE clients, and only the holder of
    /// <see cref="CampaignMetricsLeaderResources.Webhooks"/> dispatches their webhooks. The lease is registered
    /// only when the pod runs the poller (<paramref name="dialerEnabled"/>): a pod without it would otherwise be
    /// able to win the lease and leave every metrics webhook undispatched cluster-wide. Every other pod gets the
    /// <see cref="AlwaysLeader"/> stub, which is inert there because it never publishes a metrics event.
    /// </para>
    /// </summary>
    public static IServiceCollection AddClusteredLeaders(
        this IServiceCollection services,
        NpgsqlDataSource clusterDataSource,
        bool voiceInboundEnabled,
        bool voiceAmiEnabled,
        bool dialerEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clusterDataSource);

        services.AddKeyedSingleton<NpgsqlDataSource>("Cluster", (_, _) => clusterDataSource);
        services.AddPostgresDistributedLock(connectionStringName: "Cluster");
        services.AddVerbaraCluster(opts =>
        {
            opts.UsePostgresLockBackend(connectionStringName: "Cluster");
            opts.RegisterLeader(AgentLivenessLeaderResources.Sweep);   // W3 — always
            opts.RegisterLeader(PendingPauseLeaderResources.Sweep);    // W4 — always
            opts.RegisterLeader(WorkFailoverLeaderResources.Sweep);    // W5 — always
            if (voiceInboundEnabled)
                opts.RegisterLeader(VoiceLeaderResources.Inbound);
            if (voiceAmiEnabled)
            {
                opts.RegisterLeader(VoiceLeaderResources.AmiOwner);
                opts.RegisterLeader(CallbackRescueLeaderResources.Sweep);    // W5b — voice-AMI only
            }
            if (dialerEnabled)
                opts.RegisterLeader(CampaignMetricsLeaderResources.Webhooks);   // only a pod that runs the poller
        });
        if (!dialerEnabled)
            services.AddAlwaysLeader(CampaignMetricsLeaderResources.Webhooks);

        return services;
    }

    /// <summary>
    /// Registers the single-node (no cluster connection) <see cref="AlwaysLeader"/> stubs, so the leader-gated
    /// work still runs on a single replica: W3 (agent-liveness sweep), W4 (deferred-pause drain sweep), W5
    /// (work-failover sweep) and the campaign-metrics webhooks. Clustered deployments take the
    /// Postgres-lease-backed leaders of <see cref="AddClusteredLeaders"/> instead. Voice has no single-node path:
    /// it requires the cluster connection.
    /// </summary>
    public static IServiceCollection AddSingleNodeLeaders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAlwaysLeader(AgentLivenessLeaderResources.Sweep);      // W3
        services.AddAlwaysLeader(PendingPauseLeaderResources.Sweep);       // W4
        services.AddAlwaysLeader(WorkFailoverLeaderResources.Sweep);       // W5
        services.AddAlwaysLeader(CampaignMetricsLeaderResources.Webhooks);
        return services;
    }

    private static void AddAlwaysLeader(this IServiceCollection services, string resource) =>
        services.AddKeyedSingleton<IClusterLeader>(resource, (_, _) => new AlwaysLeader(resource));
}
