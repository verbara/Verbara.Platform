using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
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
        analytics.MapGet("/cdr", ListCdr);
        analytics.MapGet("/cdr/{sessionId}", GetCdrDetail);
        // QA endpoints will be added by Task 4
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

    // ─── CDR List Handler ──────────────────────────────────────────────────────

    private static async Task<IResult> ListCdr(
        HttpContext context,
        ICompletedSessionStore cdrStore,
        ICallAnalyticsStore qaStore,
        IAgentStore agentStore,
        IQueueStore queueStore,
        string? from,
        string? to,
        string? queue,
        string? agent,
        string? channel,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var tenantIdObj = new TenantId(tenantId);

        var toDate = to is not null ? DateTimeOffset.Parse(to) : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from) : toDate.AddDays(-7);

        var query = new CompletedSessionQuery
        {
            TenantId = tenantId,
            From = fromDate,
            To = toDate,
            QueueName = queue,
            AgentId = agent,
            Limit = pageSize + 1,
            Offset = (page - 1) * pageSize,
        };

        var rows = await cdrStore.QueryAsync(tenantId, query, ct);
        var hasMore = rows.Count > pageSize;
        var pageRows = hasMore ? rows.Take(pageSize).ToList() : rows.ToList();

        // Batch enrich agent names
        var agentNames = await BuildAgentNameMapAsync(pageRows.Select(r => r.AgentId).Distinct(), tenantIdObj, agentStore, ct);

        // Batch lookup QA scores
        var qaResults = new Dictionary<string, CallAnalysisResult?>();
        foreach (var row in pageRows)
        {
            var qa = await qaStore.GetAsync(row.SessionId, tenantId, ct);
            qaResults[row.SessionId] = qa;
        }

        // Batch lookup queue SLA targets (by name)
        var queueSlaMap = await BuildQueueSlaMapAsync(pageRows.Select(r => r.QueueName).Distinct(), tenantIdObj, queueStore, ct);

        var dtos = pageRows.Select(row =>
        {
            agentNames.TryGetValue(row.AgentId ?? "", out var agentName);
            qaResults.TryGetValue(row.SessionId, out var qa);
            queueSlaMap.TryGetValue(row.QueueName ?? "", out var slaThresholdMsNullable);
            var slaMet = ComputeSlaMet(row.WaitTimeMs, slaThresholdMsNullable ?? 20000L);
            var (hasScore, score) = qa is not null && qa.QualityScore is not null
                ? (true, (double?)NormalizeQaScore(qa.QualityScore))
                : (false, (double?)null);

            return new CdrRowDto(
                SessionId: row.SessionId,
                StartTime: row.StartedAt,
                AnswerTime: row.ConnectedAt,
                EndTime: row.CompletedAt,
                Contact: row.CallerIdNum ?? row.CallerIdName,
                Channel: "voice",
                QueueName: row.QueueName,
                AgentName: agentName,
                DurationMs: row.DurationMs,
                TalkTimeMs: row.TalkTimeMs,
                WaitTimeMs: row.WaitTimeMs,
                Disposition: MapDisposition(row.HangupCause, row.FinalState),
                SlaMet: slaMet,
                HasQaScore: hasScore,
                QaScore: score);
        }).ToArray();

        return Results.Ok(new { Data = dtos, HasMore = hasMore, Page = page, PageSize = pageSize });
    }

    // ─── CDR Detail Handler ────────────────────────────────────────────────────

    private static async Task<IResult> GetCdrDetail(
        string sessionId,
        HttpContext context,
        ICompletedSessionStore cdrStore,
        ICallAnalyticsStore qaStore,
        IAgentStore agentStore,
        IQueueStore queueStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var tenantIdObj = new TenantId(tenantId);

        var row = await cdrStore.GetAsync(tenantId, sessionId, ct);
        if (row is null)
            return Results.NotFound();

        var agentNames = await BuildAgentNameMapAsync([row.AgentId], tenantIdObj, agentStore, ct);
        agentNames.TryGetValue(row.AgentId ?? "", out var agentName);

        var queueSlaMap = await BuildQueueSlaMapAsync([row.QueueName], tenantIdObj, queueStore, ct);
        queueSlaMap.TryGetValue(row.QueueName ?? "", out var slaThresholdMsNullable);
        var slaMet = ComputeSlaMet(row.WaitTimeMs, slaThresholdMsNullable ?? 20000L);

        // Build timeline
        var timeline = new List<CdrTimelineEventDto>
        {
            new("started", row.StartedAt, null),
        };
        if (row.ConnectedAt is not null)
            timeline.Add(new("answered", row.ConnectedAt.Value, null));
        timeline.Add(new("ended", row.CompletedAt, MapDisposition(row.HangupCause, row.FinalState)));

        // QA lookup
        var qa = await qaStore.GetAsync(sessionId, tenantId, ct);
        CdrQaSummaryDto? qaSummary = null;
        if (qa is not null)
        {
            var normalizedScore = qa.QualityScore is not null ? (double?)NormalizeQaScore(qa.QualityScore) : null;
            qaSummary = new CdrQaSummaryDto(
                Reason: qa.Summary?.Reason,
                Outcome: qa.Summary?.Outcome,
                Narrative: qa.Summary?.Narrative,
                QaScore: normalizedScore,
                SentimentLabel: qa.Sentiment?.OverallLabel.ToString());
        }

        var (hasScore, score) = qa is not null && qa.QualityScore is not null
            ? (true, (double?)NormalizeQaScore(qa.QualityScore))
            : (false, (double?)null);

        var cdrRow = new CdrRowDto(
            SessionId: row.SessionId,
            StartTime: row.StartedAt,
            AnswerTime: row.ConnectedAt,
            EndTime: row.CompletedAt,
            Contact: row.CallerIdNum ?? row.CallerIdName,
            Channel: "voice",
            QueueName: row.QueueName,
            AgentName: agentName,
            DurationMs: row.DurationMs,
            TalkTimeMs: row.TalkTimeMs,
            WaitTimeMs: row.WaitTimeMs,
            Disposition: MapDisposition(row.HangupCause, row.FinalState),
            SlaMet: slaMet,
            HasQaScore: hasScore,
            QaScore: score);

        return Results.Ok(new CdrDetailDto(cdrRow, [.. timeline], qaSummary));
    }

    // ─── CDR Helpers ──────────────────────────────────────────────────────────

    private static string MapDisposition(int? hangupCause, int finalState)
    {
        // FinalState values: Connected=4, Completed=8, Failed=9
        if (finalState == 9) return "FAILED";
        if (hangupCause == 17) return "BUSY";
        if (hangupCause is 18 or 19) return "NO ANSWER";
        if (hangupCause == 16 && finalState == 8) return "ANSWERED";
        if (hangupCause is null && finalState == 8) return "ANSWERED";
        if (finalState == 8) return "ANSWERED";
        return "OTHER";
    }

    private static bool ComputeSlaMet(long? waitTimeMs, long thresholdMs)
        => waitTimeMs is null || waitTimeMs <= thresholdMs;

    private static double NormalizeQaScore(QaResult qa)
        => qa.MaxPossibleScore <= 0 ? 0.0 : qa.TotalScore * 100.0 / qa.MaxPossibleScore;

    private static async Task<Dictionary<string, string>> BuildAgentNameMapAsync(
        IEnumerable<string?> agentIds,
        TenantId tenantId,
        IAgentStore agentStore,
        CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        foreach (var id in agentIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (map.ContainsKey(id)) continue;
            try
            {
                var agent = await agentStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
                map[id] = agent?.DisplayName ?? id;
            }
            catch
            {
                map[id] = id;
            }
        }
        return map;
    }

    private static async Task<Dictionary<string, long?>> BuildQueueSlaMapAsync(
        IEnumerable<string?> queueNames,
        TenantId tenantId,
        IQueueStore queueStore,
        CancellationToken ct)
    {
        // IQueueStore.ListAsync returns paged queues; we scan the first page and match by name
        var map = new Dictionary<string, long?>();
        var names = queueNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (names.Count == 0) return map;

        var pagedQueues = await queueStore.ListAsync(tenantId, new PagedQuery { Page = 1, PageSize = 200 }, ct);
        foreach (var q in pagedQueues.Items)
        {
            if (q.SlaTargets?.AnswerWithinSeconds is int seconds && names.Contains(q.Name))
                map[q.Name] = seconds * 1000L;
        }
        return map;
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
