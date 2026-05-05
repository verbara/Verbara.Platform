using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Analytics;
using Verbara.Sdk.Pro.Analytics.Live;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class QueueMetricsEndpoints
{
    /// <summary>
    /// Response header set when the live queue metrics provider is either not
    /// registered or returned null for every queue in the current request.
    /// Consumed by Platform.Web to render em-dash placeholders in the queue
    /// metrics table (R5.1 Task H).
    /// </summary>
    internal const string MetricsAvailableHeader = "X-Metrics-Available";

    public static void MapQueueMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/operations")
            .RequireAuthorization("SupervisorPlus")
            .RequireLicenseFeature(LicenseFeature.Analytics);
        group.MapGet("/queue-metrics", GetQueueMetrics);
    }

    private static async Task<IResult> GetQueueMetrics(
        HttpContext context,
        [FromServices] IQueueStore queueStore,
        [FromServices] IAgentStore agentStore,
        [FromServices] IIntervalSnapshotStore snapshotStore,
        [FromServices] ILiveQueueMetricsProvider? liveMetrics,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var pagedQueues = await queueStore.ListAsync(tenantId, new PagedQuery { Page = 1, PageSize = 200 }, ct);

        // Load all agents once
        var pagedAgents = await agentStore.ListAsync(tenantId, new AgentQuery { Page = 1, PageSize = 500 }, ct);
        var allAgents = pagedAgents.Items;

        // Load recent interval snapshots (last 30 minutes) for SLA computation
        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddMinutes(-30);
        var allSnapshots = await snapshotStore.QueryAsync(tenantId, windowStart, now, null, null, ct);

        // Group snapshots by queue name for per-queue SLA lookup
        var snapshotsByQueue = allSnapshots
            .GroupBy(s => s.QueueName)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Live queue metrics (Pro.Analytics.Live v1.12.0-pro) — nullable, graceful degrade
        // when the provider is not registered (e.g., single-node deployments without
        // Postgres-backed Pro.Analytics.Live). Query per queue using the string tenant
        // id; see multi-tenant note in the task H commit message for scope caveats.
        // NOTE (R5.1 Task H known limitation): Platform registers Pro.Analytics as
        // process-scope singleton with empty DefaultTenantId, so LiveQueueSnapshotWriter
        // persists rows with tenant_id="". The endpoint therefore passes "" for
        // tenantId when querying the provider to match the persisted rows. Per-tenant
        // writer scope is tracked as a follow-up (future Platform patch / R5.2).
        var liveLookupTenantId = string.Empty;
        var anyLiveMetricsAvailable = false;

        var dtos = new List<QueueMetricsDto>(pagedQueues.Items.Count);
        foreach (var q in pagedQueues.Items)
        {
            // Count agents by state (all agents for now — queue membership not tracked at agent level)
            var available = allAgents.Count(a => a.State == AgentState.Available);
            var busy = allAgents.Count(a => a.State is AgentState.Busy or AgentState.ACW);
            var away = allAgents.Count(a => a.State is AgentState.Break or AgentState.Lunch or AgentState.Training or AgentState.DND);

            // Compute SLA% from interval snapshots (weighted aggregate across all servers)
            double slaPercent = 0.0;
            if (snapshotsByQueue.TryGetValue(q.Name, out var queueSnapshots))
            {
                var totalOfferedMinusShort = queueSnapshots.Sum(s => s.CallsOffered - s.ShortAbandons);
                var totalSlaMet = queueSnapshots.Sum(s => s.SlaMetCount);
                slaPercent = totalOfferedMinusShort > 0
                    ? totalSlaMet * 100.0 / totalOfferedMinusShort
                    : 0.0;
            }

            int? waiting = null;
            double? avgWaitSeconds = null;
            if (liveMetrics is not null)
            {
                var live = await liveMetrics.GetLiveMetricsAsync(liveLookupTenantId, q.Name, ct);
                if (live is not null)
                {
                    waiting = live.CallsWaiting;
                    avgWaitSeconds = live.AvgWaitSeconds;
                    anyLiveMetricsAvailable = true;
                }
            }

            dtos.Add(new QueueMetricsDto(
                QueueId: q.QueueId.Value,
                QueueName: q.Name,
                Waiting: waiting,
                AvgWaitSeconds: avgWaitSeconds,
                SlaPercent: slaPercent,
                AgentsAvailable: available,
                AgentsBusy: busy,
                AgentsAway: away));
        }

        // Signal unavailability either when the provider is unregistered or when
        // every per-queue lookup returned null (no rows yet in live_queue_metrics).
        if (liveMetrics is null || !anyLiveMetricsAvailable)
            context.Response.Headers[MetricsAvailableHeader] = "false";

        return Results.Ok(dtos.ToArray());
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }

}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record QueueMetricsDto(
    string QueueId,
    string QueueName,
    int? Waiting,
    double? AvgWaitSeconds,
    double SlaPercent,
    int AgentsAvailable,
    int AgentsBusy,
    int AgentsAway);
