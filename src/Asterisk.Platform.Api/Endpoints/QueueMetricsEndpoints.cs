using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;

namespace Asterisk.Platform.Api.Endpoints;

internal static class QueueMetricsEndpoints
{
    public static void MapQueueMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operations").RequireAuthorization("SupervisorPlus");
        group.MapGet("/queue-metrics", GetQueueMetrics);
    }

    private static async Task<IResult> GetQueueMetrics(
        HttpContext context,
        IQueueStore queueStore,
        IAgentStore agentStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var pagedQueues = await queueStore.ListAsync(tenantId, new PagedQuery { Page = 1, PageSize = 200 }, ct);

        // Load all agents once
        var pagedAgents = await agentStore.ListAsync(tenantId, new AgentQuery { Page = 1, PageSize = 500 }, ct);
        var allAgents = pagedAgents.Items;

        var dtos = pagedQueues.Items.Select(q =>
        {
            // Count agents by state (all agents for now — queue membership not tracked at agent level)
            var available = allAgents.Count(a => a.State == AgentState.Available);
            var busy = allAgents.Count(a => a.State is AgentState.Busy or AgentState.ACW);
            var away = allAgents.Count(a => a.State is AgentState.Break or AgentState.Lunch or AgentState.Training or AgentState.DND);

            // SLA percent not computable without live interval data; return 0 for now
            const double slaPercent = 0.0;

            return new QueueMetricsDto(
                QueueId: q.QueueId.Value,
                QueueName: q.Name,
                Waiting: 0,
                AvgWaitSeconds: 0,
                SlaPercent: slaPercent,
                AgentsAvailable: available,
                AgentsBusy: busy,
                AgentsAway: away);
        }).ToArray();

        return Results.Ok(dtos);
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
    int Waiting,
    double AvgWaitSeconds,
    double SlaPercent,
    int AgentsAvailable,
    int AgentsBusy,
    int AgentsAway);
