using System.Globalization;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Middleware;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Sdk.Pro.Analytics;
using Asterisk.Sdk.Pro.CallAnalytics.Domain;
using Asterisk.Sdk.Pro.CallAnalytics.Store;
using Asterisk.Sdk.Pro.EventStore;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var analytics = app.MapGroup("/analytics")
            .RequireAuthorization("SupervisorPlus")
            .RequireLicenseFeature(LicenseFeature.Analytics);
        analytics.MapGet("/dashboard", GetDashboard);
        analytics.MapGet("/cdr", ListCdr);
        analytics.MapGet("/cdr/{sessionId}", GetCdrDetail);
        analytics.MapGet("/qa", ListQa);
        analytics.MapGet("/qa/{sessionId}", GetQaDetail);
        analytics.MapGet("/intervals/agents", ListAgentIntervals);
        analytics.MapGet("/intervals", ListIntervals);
    }

    // ─── Dashboard Handler ─────────────────────────────────────────────────────

    private static async Task<IResult> GetDashboard(
        HttpContext context,
        [FromServices] IIntervalSnapshotStore snapshotStore,
        [FromServices] ICompletedSessionStore cdrStore,
        string? from,
        string? to,
        string? queue,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var toDate = to is not null ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture) : toDate.AddDays(-7);
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
            .GroupBy(s => s.IntervalStart.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:00", CultureInfo.InvariantCulture))
            .Select(g => new TrendPointDto(g.Key, g.Sum(s => s.CallsAnswered)))
            .OrderBy(p => p.Label)
            .ToArray();

        // SLA trend: group by date
        var slaTrend = snapshots
            .GroupBy(s => s.IntervalStart.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
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
        [FromServices] ICompletedSessionStore cdrStore,
        [FromServices] ICallAnalyticsStore qaStore,
        [FromServices] IAgentStore agentStore,
        [FromServices] IQueueStore queueStore,
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

        var toDate = to is not null ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture) : toDate.AddDays(-7);

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
                Channel: DeriveChannelType(row.Direction),
                QueueName: row.QueueName,
                AgentName: agentName,
                DurationMs: row.DurationMs,
                TalkTimeMs: row.TalkTimeMs,
                WaitTimeMs: row.WaitTimeMs,
                Disposition: MapDisposition(row.HangupCause, row.FinalState),
                SlaMet: slaMet,
                HasQaScore: hasScore,
                QaScore: score,
                SentimentLabel: MapSentimentLabel(qa?.Sentiment?.OverallLabel),
                HasRecording: !string.IsNullOrEmpty(row.RecordingName),
                TransferredTo: row.TransferredTo,
                TransferType: row.TransferType,
                HangupSource: row.HangupSource,
                WrapUpDurationMs: row.WrapUpDurationMs,
                HoldCount: row.HoldCount,
                RingDurationMs: ComputeRingDurationMs(row),
                CampaignName: null,
                DispositionName: null,
                Metadata: row.Metadata);
        }).ToArray();

        return Results.Ok(new PagedDataResponse<CdrRowDto>(dtos, hasMore, page, pageSize));
    }

    // ─── CDR Detail Handler ────────────────────────────────────────────────────

    private static async Task<IResult> GetCdrDetail(
        string sessionId,
        HttpContext context,
        [FromServices] ICompletedSessionStore cdrStore,
        [FromServices] ICallAnalyticsStore qaStore,
        [FromServices] IAgentStore agentStore,
        [FromServices] IQueueStore queueStore,
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
        if (row.TransferredTo is not null)
            timeline.Add(new("transferred", row.CompletedAt, $"{MapTransferType(row.TransferType)} to {row.TransferredTo}"));
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
            Channel: DeriveChannelType(row.Direction),
            QueueName: row.QueueName,
            AgentName: agentName,
            DurationMs: row.DurationMs,
            TalkTimeMs: row.TalkTimeMs,
            WaitTimeMs: row.WaitTimeMs,
            Disposition: MapDisposition(row.HangupCause, row.FinalState),
            SlaMet: slaMet,
            HasQaScore: hasScore,
            QaScore: score,
            SentimentLabel: MapSentimentLabel(qa?.Sentiment?.OverallLabel),
            HasRecording: !string.IsNullOrEmpty(row.RecordingName),
            TransferredTo: row.TransferredTo,
            TransferType: row.TransferType,
            HangupSource: row.HangupSource,
            WrapUpDurationMs: row.WrapUpDurationMs,
            HoldCount: row.HoldCount,
            RingDurationMs: ComputeRingDurationMs(row),
            CampaignName: null,
            DispositionName: null,
            Metadata: row.Metadata);

        var hasTranscript = qa is not null && qa.Summary is not null;
        var recordingStreamUrl = !string.IsNullOrEmpty(row.RecordingName)
            ? $"/api/recordings/{row.SessionId}/stream"
            : null;

        return Results.Ok(new CdrDetailDto(
            cdrRow,
            [.. timeline],
            qaSummary,
            CalledNumber: row.Extension,
            LinkedSessionId: row.LinkedSessionId,
            TransferCount: row.TransferCount,
            RecordingName: row.RecordingName,
            RecordingStreamUrl: recordingStreamUrl,
            HasTranscript: hasTranscript));
    }

    // ─── QA List Handler ──────────────────────────────────────────────────────

    private static async Task<IResult> ListQa(
        HttpContext context,
        [FromServices] ICallAnalyticsStore qaStore,
        [FromServices] ICompletedSessionStore cdrStore,
        [FromServices] IAgentStore agentStore,
        string? from,
        string? to,
        double? minScore,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var tenantIdObj = new TenantId(tenantId);

        var toDate = to is not null ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture) : toDate.AddDays(-7);

        // minScore arrives as 0-100; QaResult scores are stored as 0-1 fractions
        var query = new CallAnalyticsQuery
        {
            TenantId = tenantId,
            From = fromDate,
            To = toDate,
            MinQaScore = minScore.HasValue ? minScore.Value / 100.0 : null,
            Limit = pageSize + 1,
            Offset = (page - 1) * pageSize,
        };

        var results = await qaStore.QueryAsync(query, ct);
        var hasMore = results.Count > pageSize;
        var pageResults = hasMore ? results.Take(pageSize).ToList() : results.ToList();

        // Batch lookup CDR for AgentId / QueueName
        var cdrMap = new Dictionary<string, CompletedSessionRow?>();
        foreach (var r in pageResults)
        {
            var cdr = await cdrStore.GetAsync(tenantId, r.SessionId, ct);
            cdrMap[r.SessionId] = cdr;
        }

        // Batch enrich agent names
        var agentIds = cdrMap.Values
            .Where(c => c is not null)
            .Select(c => c!.AgentId)
            .Distinct();
        var agentNames = await BuildAgentNameMapAsync(agentIds, tenantIdObj, agentStore, ct);

        var dtos = pageResults.Select(r =>
        {
            cdrMap.TryGetValue(r.SessionId, out var cdr);
            agentNames.TryGetValue(cdr?.AgentId ?? "", out var agentName);

            var qaScore = r.QualityScore is not null ? NormalizeQaScore(r.QualityScore) : 0.0;
            var sentimentLabel = MapSentimentLabel(r.Sentiment?.OverallLabel);
            var narrative = r.Summary?.Narrative is { } n && n.Length > 150
                ? n[..150]
                : r.Summary?.Narrative;
            var topics = r.Topics?.AllTopics.Select(t => t.TopicName).ToArray() ?? [];

            return new QaRowDto(
                SessionId: r.SessionId,
                AnalyzedAt: r.AnalyzedAt,
                AgentName: agentName,
                QueueName: cdr?.QueueName,
                QaScore: qaScore,
                SummaryNarrative: narrative,
                HasComplianceViolations: r.ComplianceViolations.Count > 0,
                ViolationCount: r.ComplianceViolations.Count,
                SentimentLabel: sentimentLabel,
                Topics: topics);
        }).ToArray();

        return Results.Ok(new PagedDataResponse<QaRowDto>(dtos, hasMore, page, pageSize));
    }

    // ─── QA Detail Handler ─────────────────────────────────────────────────────

    private static async Task<IResult> GetQaDetail(
        string sessionId,
        HttpContext context,
        [FromServices] ICallAnalyticsStore qaStore,
        [FromServices] ICompletedSessionStore cdrStore,
        [FromServices] IAgentStore agentStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var tenantIdObj = new TenantId(tenantId);

        var result = await qaStore.GetAsync(sessionId, tenantId, ct);
        if (result is null)
            return Results.NotFound();

        var cdr = await cdrStore.GetAsync(tenantId, sessionId, ct);
        var agentNames = await BuildAgentNameMapAsync([cdr?.AgentId], tenantIdObj, agentStore, ct);
        agentNames.TryGetValue(cdr?.AgentId ?? "", out var agentName);

        var qaScore = result.QualityScore is not null ? NormalizeQaScore(result.QualityScore) : 0.0;
        var maxPossibleScore = result.QualityScore?.MaxPossibleScore ?? 0.0;

        var criteria = result.QualityScore?.Items.Select(item =>
        {
            var itemScore = item.Weight <= 0 ? 0.0 : item.Score * 100.0 / item.Weight;
            return new QaCriterionDto(
                Category: item.Category,
                Score: itemScore,
                Weight: item.Weight,
                Passed: item.Passed,
                Feedback: item.Feedback);
        }).ToArray() ?? [];

        var violations = result.ComplianceViolations.Select(v =>
            new ComplianceViolationDto(
                RuleName: v.RuleName,
                Severity: v.Severity.ToString(),
                Description: v.Description,
                Evidence: v.Evidence)).ToArray();

        var allTopics = result.Topics?.AllTopics.Select(t =>
            new TopicDto(t.TopicName, t.Confidence)).ToArray() ?? [];

        var dto = new QaDetailDto(
            SessionId: result.SessionId,
            AnalyzedAt: result.AnalyzedAt,
            AgentName: agentName,
            QueueName: cdr?.QueueName,
            Reason: result.Summary?.Reason,
            Outcome: result.Summary?.Outcome,
            Narrative: result.Summary?.Narrative,
            ActionItems: result.Summary?.ActionItems.ToArray() ?? [],
            QaScore: qaScore,
            MaxPossibleScore: maxPossibleScore,
            Criteria: criteria,
            Violations: violations,
            SentimentLabel: MapSentimentLabel(result.Sentiment?.OverallLabel),
            SentimentTrend: result.Sentiment?.Trend.ToString(),
            SentimentScore: result.Sentiment?.OverallScore,
            PrimaryTopic: result.Topics?.PrimaryTopic,
            AllTopics: allTopics,
            AgentTalkRatio: result.Metrics.AgentTalkRatio,
            SilenceCount: result.Metrics.SilenceCount,
            InterruptionCount: result.Metrics.InterruptionCount);

        return Results.Ok(dto);
    }

    // ─── Agent Intervals Handler ───────────────────────────────────────────────

    private static async Task<IResult> ListAgentIntervals(
        HttpContext context,
        [FromServices] AnalyticsQueryService svc,
        string? from,
        string? to,
        string? agentId,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var fromDt = DateTimeOffset.TryParse(from, out var f) ? f : DateTimeOffset.UtcNow.AddDays(-1);
        var toDt = DateTimeOffset.TryParse(to, out var t) ? t : DateTimeOffset.UtcNow;

        var snapshots = await svc.GetAgentIntervalsAsync(tenantId, fromDt, toDt, agentId, ct);
        var dtos = snapshots.Select(s => new AgentIntervalDto(
            s.AgentId,
            s.IntervalStart,
            s.IntervalSeconds,
            s.CallsHandled,
            s.AhtMs,
            s.OccupancyPercent,
            s.RnaCount,
            s.Transfers,
            s.TotalPauseMs,
            s.LoginDurationMs)).ToList();
        return Results.Ok(dtos);
    }

    // ─── Intervals Handler ─────────────────────────────────────────────────────

    private static async Task<IResult> ListIntervals(
        HttpContext context,
        [FromServices] IIntervalSnapshotStore snapshotStore,
        string? from,
        string? to,
        string? queue,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var toDate = to is not null ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture) : toDate.AddDays(-7);

        var snapshots = await snapshotStore.QueryAsync(tenantId, fromDate, toDate, queue, null, ct);

        var dtos = snapshots.Select(s => new IntervalDto(
            QueueName: s.QueueName,
            IntervalStart: s.IntervalStart,
            IntervalSeconds: s.IntervalSeconds,
            CallsOffered: s.CallsOffered,
            CallsAnswered: s.CallsAnswered,
            CallsAbandoned: s.CallsAbandoned,
            SlaPercent: s.SlaPercent,
            AsaMs: s.AsaMs,
            AhtMs: s.AhtMs,
            AbandonRatePercent: s.AbandonRatePercent,
            SlaMetCount: s.SlaMetCount)).ToArray();

        return Results.Ok(dtos);
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

    private static string DeriveChannelType(int direction)
        => direction == 1 ? "sip" : "voice";

    private static string MapTransferType(short? transferType)
        => transferType switch
        {
            1 => "Blind transfer",
            2 => "Attended transfer",
            _ => "Transfer",
        };

    private static long? ComputeRingDurationMs(CompletedSessionRow row)
    {
        if (row.ConnectedAt is not { } connected)
            return null;

        var totalBeforeAnswer = (long)(connected - row.StartedAt).TotalMilliseconds;
        var ring = totalBeforeAnswer - (row.WaitTimeMs ?? 0);
        return ring > 0 ? ring : 0;
    }

    private static double NormalizeQaScore(QaResult qa)
        => qa.MaxPossibleScore <= 0 ? 0.0 : qa.TotalScore * 100.0 / qa.MaxPossibleScore;

    private static string? MapSentimentLabel(SentimentLabel? label)
        => label switch
        {
            SentimentLabel.VeryNegative or SentimentLabel.Negative => "Negative",
            SentimentLabel.Neutral => "Neutral",
            SentimentLabel.Positive or SentimentLabel.VeryPositive => "Positive",
            _ => null,
        };

    private static async Task<Dictionary<string, string>> BuildAgentNameMapAsync(
        IEnumerable<string?> agentIds,
        TenantId tenantId,
        [FromServices] IAgentStore agentStore,
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
        [FromServices] IQueueStore queueStore,
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
    double? QaScore,
    string? SentimentLabel,
    bool HasRecording,
    string? TransferredTo,
    short? TransferType,
    short? HangupSource,
    long? WrapUpDurationMs,
    short HoldCount,
    long? RingDurationMs,
    string? CampaignName,
    string? DispositionName,
    Dictionary<string, string>? Metadata);

internal sealed record CdrDetailDto(
    CdrRowDto Cdr,
    CdrTimelineEventDto[] Timeline,
    CdrQaSummaryDto? QaSummary,
    string? CalledNumber,
    string? LinkedSessionId,
    short TransferCount,
    string? RecordingName,
    string? RecordingStreamUrl,
    bool HasTranscript);

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

internal sealed record AgentIntervalDto(
    string AgentId,
    DateTimeOffset IntervalStart,
    int IntervalSeconds,
    int CallsHandled,
    double AhtMs,
    double OccupancyPercent,
    int RnaCount,
    int Transfers,
    long TotalPauseMs,
    long LoginDurationMs);
