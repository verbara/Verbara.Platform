using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Analytics;
using Asterisk.Sdk.Pro.CallAnalytics.Domain;
using Asterisk.Sdk.Pro.CallAnalytics.Store;
using Asterisk.Sdk.Pro.EventStore;

namespace Asterisk.Platform.Api.Endpoints;

internal static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var analytics = app.MapGroup("/api/analytics").RequireAuthorization();
        analytics.MapGet("/dashboard", GetDashboard);
        // CDR and QA endpoints will be added by Tasks 3 and 4
    }

    // ─── Dashboard Handler ─────────────────────────────────────────────────────

    private static async Task<IResult> GetDashboard(
        HttpContext context,
        IIntervalSnapshotStore snapshotStore,
        ICompletedSessionStore cdrStore,
        string? from,
        string? to,
        string? queue,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var toDate = to is not null ? DateTimeOffset.Parse(to) : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from) : toDate.AddDays(-7);
        var period = toDate - fromDate;

        // Current period snapshots
        var snapshots = await snapshotStore.QueryAsync(tenantId, fromDate, toDate, queue, null, ct);

        // KPIs — weighted aggregation
        var kpis = ComputeKpis(snapshots);

        // Previous period KPIs
        var prevFrom = fromDate - period;
        var prevSnapshots = await snapshotStore.QueryAsync(tenantId, prevFrom, fromDate, queue, null, ct);
        var previousKpis = ComputeKpis(prevSnapshots);

        // Volume trend: group by hour
        var volumeTrend = snapshots
            .GroupBy(s => s.IntervalStart.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:00"))
            .Select(g => new TrendPointDto(g.Key, g.Sum(s => s.CallsAnswered)))
            .OrderBy(p => p.Label)
            .ToArray();

        // SLA trend: group by date
        var slaTrend = snapshots
            .GroupBy(s => s.IntervalStart.ToUniversalTime().ToString("yyyy-MM-dd"))
            .Select(g =>
            {
                var offered = g.Sum(s => s.CallsOffered - s.ShortAbandons);
                var slaMet = g.Sum(s => s.SlaMetCount);
                var sla = offered <= 0 ? 0.0 : slaMet * 100.0 / offered;
                return new TrendPointDto(g.Key, sla);
            })
            .OrderBy(p => p.Label)
            .ToArray();

        // Channel distribution: count CDR by direction
        var cdrs = await cdrStore.QueryAsync(tenantId, new CompletedSessionQuery
        {
            TenantId = tenantId,
            From = fromDate,
            To = toDate,
            Limit = 5000,
        }, ct);

        var channelDistribution = cdrs
            .GroupBy(c => c.Direction == 1 ? "outbound" : "inbound")
            .Select(g => new ChannelDistributionDto(g.Key, g.Count()))
            .ToArray();

        var dashboard = new DashboardDto(
            Kpis: kpis,
            PreviousPeriodKpis: previousKpis,
            VolumeTrend: volumeTrend,
            SlaTrend: slaTrend,
            ChannelDistribution: channelDistribution);

        return Results.Ok(dashboard);
    }

    // ─── KPI Computation ───────────────────────────────────────────────────────

    private static DashboardKpisDto ComputeKpis(IReadOnlyList<IntervalSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return new DashboardKpisDto(0, 0.0, 0.0, 0.0, 0.0);

        var totalAnswered = snapshots.Sum(s => s.CallsAnswered);
        var totalOfferedMinusShort = snapshots.Sum(s => s.CallsOffered - s.ShortAbandons);

        return new DashboardKpisDto(
            ConversationsHandled: totalAnswered,
            AvgWaitMs: (double)snapshots.Sum(s => s.TotalWaitMs) / Math.Max(totalAnswered, 1),
            AvgHandleTimeMs: (double)(snapshots.Sum(s => s.TotalTalkMs + s.TotalHoldMs + s.TotalAcwMs)) / Math.Max(totalAnswered, 1),
            SlaPercent: snapshots.Sum(s => s.SlaMetCount) * 100.0 / Math.Max(totalOfferedMinusShort, 1),
            AbandonRatePercent: (snapshots.Sum(s => s.CallsAbandoned) - snapshots.Sum(s => s.ShortAbandons)) * 100.0 / Math.Max(totalOfferedMinusShort, 1));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Dashboard DTOs ────────────────────────────────────────────────────────────

internal sealed record DashboardDto(
    DashboardKpisDto Kpis,
    DashboardKpisDto? PreviousPeriodKpis,
    TrendPointDto[] VolumeTrend,
    TrendPointDto[] SlaTrend,
    ChannelDistributionDto[] ChannelDistribution);

internal sealed record DashboardKpisDto(
    int ConversationsHandled,
    double AvgWaitMs,
    double AvgHandleTimeMs,
    double SlaPercent,
    double AbandonRatePercent);

internal sealed record TrendPointDto(string Label, double Value);

internal sealed record ChannelDistributionDto(string Channel, int Count);

// ─── CDR DTOs ──────────────────────────────────────────────────────────────────

internal sealed record CdrRowDto(
    string SessionId,
    DateTimeOffset StartTime,
    DateTimeOffset? AnswerTime,
    DateTimeOffset EndTime,
    string? Contact,
    string Channel,
    string? QueueName,
    string? AgentName,
    long DurationMs,
    long? TalkTimeMs,
    long? WaitTimeMs,
    string Disposition,
    bool SlaMet,
    bool HasQaScore,
    double? QaScore);

internal sealed record CdrDetailDto(
    CdrRowDto Cdr,
    CdrTimelineEventDto[] Timeline,
    CdrQaSummaryDto? QaSummary);

internal sealed record CdrTimelineEventDto(
    string Event,
    DateTimeOffset Timestamp,
    string? Detail);

internal sealed record CdrQaSummaryDto(
    string? Reason,
    string? Outcome,
    string? Narrative,
    double? QaScore,
    string? SentimentLabel);

// ─── QA DTOs ───────────────────────────────────────────────────────────────────

internal sealed record QaRowDto(
    string SessionId,
    DateTimeOffset AnalyzedAt,
    string? AgentName,
    string? QueueName,
    double QaScore,
    string? SummaryNarrative,
    bool HasComplianceViolations,
    int ViolationCount,
    string? SentimentLabel,
    string[] Topics);

internal sealed record QaDetailDto(
    string SessionId,
    DateTimeOffset AnalyzedAt,
    string? AgentName,
    string? QueueName,
    string? Reason,
    string? Outcome,
    string? Narrative,
    string[] ActionItems,
    double QaScore,
    double MaxPossibleScore,
    QaCriterionDto[] Criteria,
    ComplianceViolationDto[] Violations,
    string? SentimentLabel,
    string? SentimentTrend,
    float? SentimentScore,
    string? PrimaryTopic,
    TopicDto[] AllTopics,
    double? AgentTalkRatio,
    int? SilenceCount,
    int? InterruptionCount);

internal sealed record QaCriterionDto(
    string Category,
    double Score,
    double Weight,
    bool Passed,
    string? Feedback);

internal sealed record ComplianceViolationDto(
    string RuleName,
    string Severity,
    string Description,
    string? Evidence);

internal sealed record TopicDto(string Name, float Confidence);

// ─── Interval DTOs ─────────────────────────────────────────────────────────────

internal sealed record IntervalDto(
    string QueueName,
    DateTimeOffset IntervalStart,
    int IntervalSeconds,
    int CallsOffered,
    int CallsAnswered,
    int CallsAbandoned,
    double SlaPercent,
    double AsaMs,
    double AhtMs,
    double AbandonRatePercent,
    int SlaMetCount);
