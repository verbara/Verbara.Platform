using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Analytics;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class AnalyticsLiveEndpoints
{
    public static void MapAnalyticsLiveEndpoints(this IEndpointRouteBuilder app)
    {
        var live = app.MapGroup("/analytics")
            .RequireAuthorization("SupervisorPlus")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.Analytics);
        live.MapGet("/live", GetAllLiveStates);
        live.MapGet("/live/{queueName}", GetLiveState);
        live.MapGet("/current-interval", GetCurrentInterval);
    }

    // ─── All Live States ───────────────────────────────────────────────────────

    private static async Task<Ok<List<LiveStateDto>>> GetAllLiveStates(
        HttpContext context,
        [FromServices] AnalyticsQueryService svc,
        [FromServices] IQueueStore queueStore,
        CancellationToken ct)
    {
        var allowedQueues = await GetTenantQueueNames(context, queueStore, ct);
        var states = svc.GetAllLiveStates(allowedQueues);
        var dtos = states.Select(s => new LiveStateDto(
            s.QueueName, s.CallsWaiting, s.LongestWaitMs,
            s.AgentsAvailable, s.AgentsOnCall, s.AgentsPaused, s.AgentsInWrapUp)).ToList();
        return TypedResults.Ok(dtos);
    }

    // ─── Live State by Queue ───────────────────────────────────────────────────

    private static async Task<Results<Ok<LiveStateDto>, NotFound>> GetLiveState(
        string queueName,
        HttpContext context,
        [FromServices] AnalyticsQueryService svc,
        [FromServices] IQueueStore queueStore,
        CancellationToken ct)
    {
        var allowedQueues = await GetTenantQueueNames(context, queueStore, ct);
        var state = svc.GetLiveState(queueName, allowedQueues);
        if (state is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(new LiveStateDto(
            state.QueueName, state.CallsWaiting, state.LongestWaitMs,
            state.AgentsAvailable, state.AgentsOnCall, state.AgentsPaused, state.AgentsInWrapUp));
    }

    // ─── Current Interval ─────────────────────────────────────────────────────

    private static async Task<Results<Ok<CurrentIntervalDto>, NotFound>> GetCurrentInterval(
        HttpContext context,
        [FromServices] AnalyticsQueryService svc,
        [FromServices] IQueueStore queueStore,
        string? queueName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queueName))
            return TypedResults.NotFound();

        var allowedQueues = await GetTenantQueueNames(context, queueStore, ct);
        var snapshot = svc.GetCurrentInterval(queueName, allowedQueues);
        if (snapshot is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(new CurrentIntervalDto(
            snapshot.IntervalStart,
            snapshot.IntervalStart.AddSeconds(snapshot.IntervalSeconds),
            snapshot.CallsOffered, snapshot.CallsAnswered, snapshot.CallsAbandoned,
            snapshot.AhtMs, snapshot.AsaMs, snapshot.SlaPercent, snapshot.AbandonRatePercent));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlySet<string>> GetTenantQueueNames(
        HttpContext context, IQueueStore queueStore, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var result = await queueStore.ListAsync(new TenantId(tenantId), new PagedQuery(1, 1000), ct);
        return result.Items.Select(q => q.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;
        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Live State DTOs ───────────────────────────────────────────────────────────

public sealed record LiveStateDto(
    string QueueName,
    int CallsWaiting,
    long LongestWaitMs,
    int AgentsAvailable,
    int AgentsOnCall,
    int AgentsPaused,
    int AgentsInWrapUp);

public sealed record CurrentIntervalDto(
    DateTimeOffset IntervalStart,
    DateTimeOffset IntervalEnd,
    int CallsOffered,
    int CallsAnswered,
    int CallsAbandoned,
    double AhtMs,
    double AsaMs,
    double SlaPercent,
    double AbandonRatePercent);
