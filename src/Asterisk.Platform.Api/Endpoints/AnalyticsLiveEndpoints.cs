using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Analytics;

namespace Asterisk.Platform.Api.Endpoints;

internal static class AnalyticsLiveEndpoints
{
    public static void MapAnalyticsLiveEndpoints(this IEndpointRouteBuilder app)
    {
        var live = app.MapGroup("/api/analytics").RequireAuthorization("SupervisorPlus");
        live.MapGet("/live", GetAllLiveStates);
        live.MapGet("/live/{queueName}", GetLiveState);
        live.MapGet("/current-interval", GetCurrentInterval);
    }

    // ─── All Live States ───────────────────────────────────────────────────────

    private static IResult GetAllLiveStates(AnalyticsQueryService svc)
    {
        var states = svc.GetAllLiveStates();
        var dtos = states.Select(s => new LiveStateDto(
            s.QueueName,
            s.CallsWaiting,
            s.LongestWaitMs,
            s.AgentsAvailable,
            s.AgentsOnCall,
            s.AgentsPaused,
            s.AgentsInWrapUp)).ToList();
        return Results.Ok(dtos);
    }

    // ─── Live State by Queue ───────────────────────────────────────────────────

    private static IResult GetLiveState(string queueName, AnalyticsQueryService svc)
    {
        var state = svc.GetLiveState(queueName);
        if (state is null)
            return Results.NotFound();

        var dto = new LiveStateDto(
            state.QueueName,
            state.CallsWaiting,
            state.LongestWaitMs,
            state.AgentsAvailable,
            state.AgentsOnCall,
            state.AgentsPaused,
            state.AgentsInWrapUp);
        return Results.Ok(dto);
    }

    // ─── Current Interval ─────────────────────────────────────────────────────

    private static IResult GetCurrentInterval(AnalyticsQueryService svc, string? queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
            return Results.NotFound();

        var snapshot = svc.GetCurrentInterval(queueName);
        if (snapshot is null)
            return Results.NotFound();

        var dto = new CurrentIntervalDto(
            snapshot.IntervalStart,
            snapshot.IntervalStart.AddSeconds(snapshot.IntervalSeconds),
            snapshot.CallsOffered,
            snapshot.CallsAnswered,
            snapshot.CallsAbandoned,
            snapshot.AhtMs,
            snapshot.AsaMs,
            snapshot.SlaPercent,
            snapshot.AbandonRatePercent);
        return Results.Ok(dto);
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
