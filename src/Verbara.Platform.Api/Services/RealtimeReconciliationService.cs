using System.Diagnostics.Metrics;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// ADR-0026 Phase B bridge worker — periodically reconciles Verbara
/// <c>queue_memberships</c> into the Asterisk Realtime store. Catches up any
/// <c>IRealtimeSyncService</c> writes that the call sites in
/// <c>QueueMembersEndpoints</c> / <c>AdminEndpoints</c> swallowed with their
/// best-effort try/catch when Asterisk Realtime is briefly unavailable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design — forward-only convergent reconciler.</b> Rather than diffing
/// against live Asterisk state (which would need an <c>IAmiConnection</c> per
/// tenant — impractical from a hosted service), each tick re-issues
/// <see cref="IRealtimeSyncService.AddQueueMemberAsync"/> for every
/// non-excluded membership in every active tenant. The SDK v2.6.0-pro voice
/// gate handles <see cref="QueueMembership.AllowedChannels"/> uniformly:
/// </para>
/// <list type="bullet">
///   <item><description><c>null</c> or includes <c>"voice"</c> → idempotent upsert (no-op when already present).</description></item>
///   <item><description>populated and excludes <c>"voice"</c> → short-circuits to <see cref="IRealtimeSyncService.RemoveQueueMemberAsync"/> (idempotent when row absent).</description></item>
/// </list>
/// <para>
/// <b>Out of scope.</b> Orphan rows in Asterisk Realtime with no corresponding
/// Verbara membership are NOT detected here — they are handled by the
/// <c>scripts/infer-memberships-from-skills.sh</c> one-shot for legacy upgrades
/// and by <see cref="IRealtimeSyncService.CleanupTenantAsync"/> during tenant
/// deletion. The SDK Pro internal reconciler (separate concern) handles
/// Realtime-store ↔ live-Asterisk drift.
/// </para>
/// <para>
/// <b>Cadence</b>: <see cref="RealtimeOptions.ReconcilerIntervalSeconds"/>
/// (default 60). Pattern mirrors <see cref="ConversationTimeoutWorker"/> —
/// <see cref="PeriodicTimer"/> + keyed <see cref="ResiliencePolicy"/> +
/// <see cref="IServiceHeartbeat"/> + <c>StopHost</c> fatal behavior.
/// </para>
/// <para>
/// <b>Meter</b>: <c>Verbara.Platform.Realtime.Reconciliation</c> exposes
/// <c>realtime.reconciliation.{tenants,memberships,sync_failures}</c> so
/// Grafana can show drift activity and silent-failure rate.
/// </para>
/// </remarks>
internal sealed partial class RealtimeReconciliationService : BackgroundService
{
    /// <summary>Keyed-service name for the per-tick <see cref="ResiliencePolicy"/>.</summary>
    public const string ResiliencePolicyKey = "worker.realtime-reconciliation";

    /// <summary>Public meter name (consumed by the OpenTelemetry registration).</summary>
    public const string MeterName = "Verbara.Platform.Realtime.Reconciliation";

    private readonly IServiceProvider _services;
    private readonly IServiceHeartbeat _heartbeat;
    private readonly TimeSpan _interval;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<RealtimeReconciliationService> _logger;

    private readonly Meter _meter;
    private readonly Counter<long> _tenantsProcessed;
    private readonly Counter<long> _membershipsReconciled;
    private readonly Counter<long> _syncFailures;

    public RealtimeReconciliationService(
        IServiceProvider services,
        IServiceHeartbeat heartbeat,
        Microsoft.Extensions.Options.IOptions<RealtimeOptions> options,
        ILogger<RealtimeReconciliationService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(heartbeat);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _heartbeat = heartbeat;
        var intervalSeconds = Math.Max(5, options.Value.ReconcilerIntervalSeconds);
        _interval = TimeSpan.FromSeconds(intervalSeconds);
        _policy = policy ?? ResiliencePolicy.NoOp;
        _logger = logger;

        _meter = new Meter(MeterName);
        _tenantsProcessed = _meter.CreateCounter<long>("realtime.reconciliation.tenants",
            description: "Tenants visited per reconciliation tick.");
        _membershipsReconciled = _meter.CreateCounter<long>("realtime.reconciliation.memberships",
            description: "queue_memberships rows re-issued to IRealtimeSyncService per tick.");
        _syncFailures = _meter.CreateCounter<long>("realtime.reconciliation.sync_failures",
            description: "AddQueueMemberAsync calls that threw during reconciliation (counted, not retried inline).");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Stagger by 10 s after boot so the foreground sync paths get a
            // chance to land first (QueueMembersEndpoints + AdminEndpoints fire
            // synchronously on every membership write).
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            using var timer = new PeriodicTimer(_interval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _heartbeat.RecordTick(nameof(RealtimeReconciliationService), _interval);

                try
                {
                    await _policy.ExecuteAsync(
                        ResiliencePolicyKey,
                        async innerCt =>
                        {
                            await ReconcileAsync(innerCt);
                            return 0;
                        },
                        stoppingToken);
                }
                catch (CircuitBreakerOpenException)
                {
                    LogCircuitOpen();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogReconcileCycleError(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(RealtimeReconciliationService), fatalEx.Message, fatalEx);
            throw;
        }
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        // Resolve a request-scoped service provider so transient stores get a
        // fresh scope per tick (Postgres connection lifetimes, etc.).
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // IRealtimeSyncService is registered only when Pro.Realtime is wired
        // (connection string present). Skip cleanly when absent — the SMB
        // single-host happy path does provide it; lab tear-downs may not.
        var syncService = sp.GetService<IRealtimeSyncService>();
        if (syncService is null)
        {
            LogSyncServiceMissing();
            return;
        }

        var tenantStore = sp.GetRequiredService<ITenantStore>();
        var membershipStore = sp.GetRequiredService<IQueueMembershipStore>();
        var queueStore = sp.GetRequiredService<IQueueStore>();
        var agentStore = sp.GetRequiredService<IAgentStore>();

        var tenants = await tenantStore.GetAllActiveAsync(ct);

        foreach (var tenant in tenants)
        {
            ct.ThrowIfCancellationRequested();

            var tid = new TenantId(tenant.TenantId);
            await ReconcileTenantAsync(
                tid, tenant.TenantId, membershipStore, queueStore, agentStore, syncService, ct);

            _tenantsProcessed.Add(1, new KeyValuePair<string, object?>("tenant", tenant.TenantId));
        }
    }

    private async Task ReconcileTenantAsync(
        TenantId tid,
        string tenantId,
        IQueueMembershipStore membershipStore,
        IQueueStore queueStore,
        IAgentStore agentStore,
        IRealtimeSyncService syncService,
        CancellationToken ct)
    {
        var memberships = await membershipStore.ListByTenantAsync(tid, ct);
        if (memberships.Count == 0)
            return;

        // Cache queues + agents within the tenant so we don't re-hit the store
        // for every membership row.
        var queueCache = new Dictionary<string, Queue?>(StringComparer.Ordinal);
        var agentCache = new Dictionary<string, Agent?>(StringComparer.Ordinal);

        foreach (var m in memberships)
        {
            if (m.IsExcluded)
                continue;

            var queueIdValue = m.QueueId.Value;
            if (!queueCache.TryGetValue(queueIdValue, out var queue))
            {
                queue = await queueStore.GetByIdAsync(tid, m.QueueId, ct);
                queueCache[queueIdValue] = queue;
            }
            if (queue is null)
                continue;

            var agentIdValue = m.AgentId.Value;
            if (!agentCache.TryGetValue(agentIdValue, out var agent))
            {
                agent = await agentStore.GetByIdAsync(tid, m.AgentId, ct);
                agentCache[agentIdValue] = agent;
            }
            if (agent is null)
                continue;

            try
            {
                await syncService.AddQueueMemberAsync(
                    tenantId, queue.Name, agent.AgentId.Value, agent.DisplayName,
                    Math.Clamp(m.Penalty, 0, 10),
                    allowedChannels: m.AllowedChannels, ct);
                _membershipsReconciled.Add(1, new KeyValuePair<string, object?>("tenant", tenantId));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _syncFailures.Add(1,
                    new KeyValuePair<string, object?>("tenant", tenantId),
                    new KeyValuePair<string, object?>("queue", queue.Name));
                LogSyncFailure(tenantId, queue.Name, agent.AgentId.Value, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Realtime reconciliation skipped — IRealtimeSyncService not registered (Pro.Realtime connection string absent).")]
    private partial void LogSyncServiceMissing();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Realtime reconciliation failed for tenant {TenantId} queue {QueueName} agent {AgentId}.")]
    private partial void LogSyncFailure(string tenantId, string queueName, string agentId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Realtime reconciliation cycle failed.")]
    private partial void LogReconcileCycleError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.realtime-reconciliation — skipping tick.")]
    private partial void LogCircuitOpen();

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
