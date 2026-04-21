using System.Globalization;
using Asterisk.Platform.Api.Middleware;
using Asterisk.Sdk.Pro.CallAnalytics.Domain;
using Asterisk.Sdk.Pro.CallAnalytics.Store;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class CallAnalyticsEndpoints
{
    private const string StoreNotConfigured = "Call analytics store not configured";

    public static void MapCallAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/call-analytics")
            .RequireAuthorization("SupervisorPlus")
            .RequireLicenseFeature(LicenseFeature.Analytics);

        group.MapGet("/results", ListResults);
        group.MapGet("/results/{sessionId}", GetResult);
        group.MapGet("/topics/trends", GetTopicTrends);
    }

    // ─── List Results ──────────────────────────────────────────────────────────

    private static async Task<IResult> ListResults(
        HttpContext context,
        IServiceProvider services,
        string? from,
        string? to,
        string? queueName,
        string? agentId,
        double? minQaScore,
        double? maxQaScore,
        bool? hasViolations,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var store = services.GetService<ICallAnalyticsStore>();
        if (store is null)
            return Results.Problem(StoreNotConfigured, statusCode: 503);

        var tenantId = GetTenantId(context);

        limit = Math.Min(limit, 200);

        var toDate = to is not null ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture) : toDate.AddDays(-7);

        var query = new CallAnalyticsQuery
        {
            TenantId = tenantId,
            From = fromDate,
            To = toDate,
            QueueName = queueName,
            AgentId = agentId,
            MinQaScore = minQaScore,
            MaxQaScore = maxQaScore,
            HasComplianceViolations = hasViolations,
            Limit = limit,
            Offset = offset,
        };

        var results = await store.QueryAsync(query, ct);

        var dtos = results.Select(MapToSummaryDto).ToArray();

        return Results.Ok(new CallAnalyticsListResponse(dtos, limit, offset, dtos.Length));
    }

    // ─── Get Detail ────────────────────────────────────────────────────────────

    private static async Task<IResult> GetResult(
        string sessionId,
        HttpContext context,
        IServiceProvider services,
        CancellationToken ct)
    {
        var store = services.GetService<ICallAnalyticsStore>();
        if (store is null)
            return Results.Problem(StoreNotConfigured, statusCode: 503);

        var tenantId = GetTenantId(context);

        var result = await store.GetAsync(sessionId, tenantId, ct);
        if (result is null)
            return Results.NotFound();

        return Results.Ok(MapToDetailDto(result));
    }

    // ─── Topic Trends ──────────────────────────────────────────────────────────

    private static async Task<IResult> GetTopicTrends(
        HttpContext context,
        IServiceProvider services,
        string? from,
        string? to,
        int topN = 10,
        CancellationToken ct = default)
    {
        var store = services.GetService<ICallAnalyticsStore>();
        if (store is null)
            return Results.Problem(StoreNotConfigured, statusCode: 503);

        var tenantId = GetTenantId(context);

        topN = Math.Min(topN, 50);

        var toDate = to is not null ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture) : toDate.AddDays(-30);

        var query = new CallAnalyticsQuery
        {
            TenantId = tenantId,
            From = fromDate,
            To = toDate,
            Limit = 1000,
        };

        var results = await store.QueryAsync(query, ct);

        // Aggregate topic occurrences + confidence scores in-memory
        var topicOccurrences = new Dictionary<string, (int Count, double TotalConfidence)>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in results)
        {
            if (r.Topics is null) continue;

            // Primary topic (confidence 1.0 if not in AllTopics)
            if (!string.IsNullOrWhiteSpace(r.Topics.PrimaryTopic))
            {
                var primary = r.Topics.PrimaryTopic;
                var primaryConfidence = r.Topics.AllTopics
                    .FirstOrDefault(t => string.Equals(t.TopicName, primary, StringComparison.OrdinalIgnoreCase))
                    ?.Confidence ?? 1.0f;

                if (topicOccurrences.TryGetValue(primary, out var existing))
                    topicOccurrences[primary] = (existing.Count + 1, existing.TotalConfidence + primaryConfidence);
                else
                    topicOccurrences[primary] = (1, primaryConfidence);
            }

            // All topics
            foreach (var match in r.Topics.AllTopics)
            {
                if (string.IsNullOrWhiteSpace(match.TopicName)) continue;
                // Skip if already counted as primary for this result
                if (string.Equals(match.TopicName, r.Topics.PrimaryTopic, StringComparison.OrdinalIgnoreCase)) continue;

                if (topicOccurrences.TryGetValue(match.TopicName, out var ex))
                    topicOccurrences[match.TopicName] = (ex.Count + 1, ex.TotalConfidence + match.Confidence);
                else
                    topicOccurrences[match.TopicName] = (1, match.Confidence);
            }
        }

        var trends = topicOccurrences
            .OrderByDescending(kv => kv.Value.Count)
            .Take(topN)
            .Select(kv => new TopicTrendDto(
                Topic: kv.Key,
                Occurrences: kv.Value.Count,
                AvgConfidence: kv.Value.Count > 0 ? kv.Value.TotalConfidence / kv.Value.Count : 0.0))
            .ToArray();

        return Results.Ok(new TopicTrendsResponse(trends, results.Count));
    }

    // ─── Mapping Helpers ───────────────────────────────────────────────────────

    private static CallAnalyticsSummaryDto MapToSummaryDto(CallAnalysisResult r)
    {
        var qaScore = r.QualityScore is { } qs && qs.MaxPossibleScore > 0
            ? (double?)(qs.TotalScore / qs.MaxPossibleScore)
            : null;

        var summaryText = r.Summary?.Narrative is { Length: > 200 } n
            ? n[..200]
            : r.Summary?.Narrative;

        return new CallAnalyticsSummaryDto(
            SessionId: r.SessionId,
            AnalyzedAt: r.AnalyzedAt,
            QaScore: qaScore,
            HasViolations: r.ComplianceViolations.Count > 0,
            ViolationCount: r.ComplianceViolations.Count,
            PrimaryTopic: r.Topics?.PrimaryTopic,
            SentimentLabel: MapSentimentLabel(r.Sentiment?.OverallLabel),
            SummaryText: summaryText,
            AgentId: null,
            QueueName: null);
    }

    private static CallAnalyticsDetailDto MapToDetailDto(CallAnalysisResult r)
    {
        var qaScore = r.QualityScore is { } qs && qs.MaxPossibleScore > 0
            ? (double?)(qs.TotalScore / qs.MaxPossibleScore)
            : null;

        var criteria = r.QualityScore?.Items.Select(item =>
        {
            var itemScore = item.Weight <= 0 ? 0.0 : item.Score * 100.0 / item.Weight;
            return new CallAnalyticsQaCriterionDto(
                Category: item.Category,
                Score: itemScore,
                Weight: item.Weight,
                Passed: item.Passed,
                Feedback: item.Feedback);
        }).ToArray() ?? [];

        var violations = r.ComplianceViolations.Select(v =>
            new CallAnalyticsViolationDto(
                RuleId: v.RuleId,
                RuleName: v.RuleName,
                Severity: v.Severity.ToString(),
                Description: v.Description,
                Evidence: v.Evidence,
                TranscriptIndex: v.TranscriptIndex)).ToArray();

        var allTopics = r.Topics?.AllTopics.Select(t =>
            new CallAnalyticsTopicMatchDto(t.TopicId, t.TopicName, t.Confidence)).ToArray() ?? [];

        return new CallAnalyticsDetailDto(
            SessionId: r.SessionId,
            TenantId: r.TenantId,
            AnalyzedAt: r.AnalyzedAt,
            ProcessingTimeMs: (long)r.ProcessingTime.TotalMilliseconds,
            SummaryReason: r.Summary?.Reason,
            SummaryOutcome: r.Summary?.Outcome,
            SummaryNarrative: r.Summary?.Narrative,
            SummaryActionItems: r.Summary?.ActionItems.ToArray() ?? [],
            QaScore: qaScore,
            QaMaxPossibleScore: r.QualityScore?.MaxPossibleScore,
            QaCriteria: criteria,
            Violations: violations,
            SentimentLabel: MapSentimentLabel(r.Sentiment?.OverallLabel),
            SentimentTrend: r.Sentiment?.Trend.ToString(),
            SentimentScore: r.Sentiment?.OverallScore,
            PrimaryTopic: r.Topics?.PrimaryTopic,
            AllTopics: allTopics,
            AgentTalkRatio: r.Metrics.AgentTalkRatio,
            SilenceCount: r.Metrics.SilenceCount,
            InterruptionCount: r.Metrics.InterruptionCount,
            AgentTurnCount: r.Metrics.AgentTurnCount,
            CallerTurnCount: r.Metrics.CallerTurnCount);
    }

    private static string? MapSentimentLabel(SentimentLabel? label)
        => label switch
        {
            SentimentLabel.VeryNegative or SentimentLabel.Negative => "Negative",
            SentimentLabel.Neutral => "Neutral",
            SentimentLabel.Positive or SentimentLabel.VeryPositive => "Positive",
            _ => null,
        };

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is Asterisk.Platform.Core.TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Call Analytics DTOs ──────────────────────────────────────────────────────

internal sealed record CallAnalyticsSummaryDto(
    string SessionId,
    DateTimeOffset AnalyzedAt,
    double? QaScore,
    bool HasViolations,
    int ViolationCount,
    string? PrimaryTopic,
    string? SentimentLabel,
    string? SummaryText,
    string? AgentId,
    string? QueueName);

internal sealed record CallAnalyticsListResponse(
    IReadOnlyList<CallAnalyticsSummaryDto> Results,
    int Limit,
    int Offset,
    int ReturnedCount);

internal sealed record CallAnalyticsDetailDto(
    string SessionId,
    string TenantId,
    DateTimeOffset AnalyzedAt,
    long ProcessingTimeMs,
    string? SummaryReason,
    string? SummaryOutcome,
    string? SummaryNarrative,
    string[] SummaryActionItems,
    double? QaScore,
    double? QaMaxPossibleScore,
    CallAnalyticsQaCriterionDto[] QaCriteria,
    CallAnalyticsViolationDto[] Violations,
    string? SentimentLabel,
    string? SentimentTrend,
    float? SentimentScore,
    string? PrimaryTopic,
    CallAnalyticsTopicMatchDto[] AllTopics,
    double AgentTalkRatio,
    int SilenceCount,
    int InterruptionCount,
    int AgentTurnCount,
    int CallerTurnCount);

internal sealed record CallAnalyticsQaCriterionDto(
    string Category,
    double Score,
    double Weight,
    bool Passed,
    string? Feedback);

internal sealed record CallAnalyticsViolationDto(
    string RuleId,
    string RuleName,
    string Severity,
    string Description,
    string? Evidence,
    int? TranscriptIndex);

internal sealed record CallAnalyticsTopicMatchDto(
    string TopicId,
    string TopicName,
    float Confidence);

internal sealed record TopicTrendDto(
    string Topic,
    int Occurrences,
    double AvgConfidence);

internal sealed record TopicTrendsResponse(
    TopicTrendDto[] Trends,
    int TotalAnalyzed);
