using System.Globalization;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.CallAnalytics.Domain;
using Verbara.Sdk.Pro.CallAnalytics.Store;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class CallAnalyticsEndpoints
{
    private const string StoreNotConfigured = "Call analytics store not configured";

    public static void MapCallAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/call-analytics")
            .RequireAuthorization("SupervisorPlus")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.Analytics);

        group.MapGet("/topics/trends", GetTopicTrends);
        group.MapGet("/sentiment/trends", GetSentimentTrends);
        group.MapGet("/compliance/summary", GetComplianceSummary);
    }

    // ─── Topic Trends ──────────────────────────────────────────────────────────

    private static async Task<Results<Ok<TopicTrendsResponse>, ProblemHttpResult>> GetTopicTrends(
        HttpContext context,
        IServiceProvider services,
        string? from,
        string? to,
        int topN = 10,
        CancellationToken ct = default)
    {
        var store = services.GetService<ICallAnalyticsStore>();
        if (store is null)
            return TypedResults.Problem(StoreNotConfigured, statusCode: 503);

        var tenantId = GetTenantId(context);

        topN = Math.Min(topN, 50);

        var toDate = to is not null ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture).ToUtcInstant() : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture).ToUtcInstant() : toDate.AddDays(-30);

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

        return TypedResults.Ok(new TopicTrendsResponse(trends, results.Count));
    }

    // ─── Sentiment Trends ─────────────────────────────────────────────────────

    private static async Task<Results<Ok<SentimentTrendsResponse>, ProblemHttpResult>> GetSentimentTrends(
        HttpContext context,
        IServiceProvider services,
        string? from,
        string? to,
        string bucket = "day",
        string? queueName = null,
        CancellationToken ct = default)
    {
        var store = services.GetService<ICallAnalyticsStore>();
        if (store is null)
            return TypedResults.Problem(StoreNotConfigured, statusCode: 503);

        var tenantId = GetTenantId(context);

        var toDate = to is not null ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture).ToUtcInstant() : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture).ToUtcInstant() : toDate.AddDays(-7);

        var query = new CallAnalyticsQuery
        {
            TenantId = tenantId,
            From = fromDate,
            To = toDate,
            QueueName = queueName,
            Limit = 2000,
        };

        var results = await store.QueryAsync(query, ct);

        // Group by bucket key
        var buckets = new Dictionary<DateTimeOffset, SentimentBucketAggregate>();

        foreach (var r in results)
        {
            var bucketStart = TruncateToBucket(r.AnalyzedAt, bucket);

            if (!buckets.TryGetValue(bucketStart, out var agg))
            {
                agg = new SentimentBucketAggregate();
                buckets[bucketStart] = agg;
            }

            agg.TotalCount++;

            if (r.Sentiment is not null)
            {
                agg.SentimentScoreSum += r.Sentiment.OverallScore;
                agg.SentimentCount++;

                switch (r.Sentiment.OverallLabel)
                {
                    case SentimentLabel.Positive or SentimentLabel.VeryPositive:
                        agg.PositiveCount++;
                        break;
                    case SentimentLabel.Neutral:
                        agg.NeutralCount++;
                        break;
                    case SentimentLabel.Negative or SentimentLabel.VeryNegative:
                        agg.NegativeCount++;
                        break;
                }
            }
        }

        var points = buckets
            .OrderBy(kv => kv.Key)
            .Select(kv => new SentimentTrendPointDto(
                BucketStart: kv.Key,
                AvgSentimentScore: kv.Value.SentimentCount > 0
                    ? (double?)kv.Value.SentimentScoreSum / kv.Value.SentimentCount
                    : null,
                PositiveCount: kv.Value.PositiveCount,
                NeutralCount: kv.Value.NeutralCount,
                NegativeCount: kv.Value.NegativeCount,
                TotalCount: kv.Value.TotalCount))
            .ToArray();

        return TypedResults.Ok(new SentimentTrendsResponse(points, bucket, fromDate, toDate));
    }

    // ─── Compliance Summary ───────────────────────────────────────────────────

    private static async Task<Results<Ok<ComplianceSummaryResponse>, ProblemHttpResult>> GetComplianceSummary(
        HttpContext context,
        IServiceProvider services,
        string? from,
        string? to,
        string? queueName = null,
        string? severity = null,
        CancellationToken ct = default)
    {
        var store = services.GetService<ICallAnalyticsStore>();
        if (store is null)
            return TypedResults.Problem(StoreNotConfigured, statusCode: 503);

        var tenantId = GetTenantId(context);

        var toDate = to is not null ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture).ToUtcInstant() : DateTimeOffset.UtcNow;
        var fromDate = from is not null ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture).ToUtcInstant() : toDate.AddDays(-7);

        var query = new CallAnalyticsQuery
        {
            TenantId = tenantId,
            From = fromDate,
            To = toDate,
            QueueName = queueName,
            HasComplianceViolations = true,
            Limit = 2000,
        };

        var results = await store.QueryAsync(query, ct);

        // Parse optional severity filter
        ComplianceSeverity? severityFilter = severity is not null
            ? Enum.Parse<ComplianceSeverity>(severity, ignoreCase: true)
            : null;

        // Aggregate by (RuleId, Severity)
        var ruleAggregates = new Dictionary<(string RuleId, ComplianceSeverity Severity), ComplianceRuleAggregate>(
            EqualityComparer<(string, ComplianceSeverity)>.Default);

        int totalInfo = 0, totalWarning = 0, totalCritical = 0;
        var sessionsWithViolations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in results)
        {
            var violations = r.ComplianceViolations;
            if (violations.Count == 0) continue;

            var sessionHasViolation = false;

            foreach (var v in violations)
            {
                if (severityFilter.HasValue && v.Severity != severityFilter.Value) continue;

                sessionHasViolation = true;

                var key = (v.RuleId, v.Severity);
                if (!ruleAggregates.TryGetValue(key, out var agg))
                {
                    agg = new ComplianceRuleAggregate
                    {
                        RuleName = v.RuleName,
                        FirstSeen = r.AnalyzedAt,
                        LastSeen = r.AnalyzedAt,
                    };
                    ruleAggregates[key] = agg;
                }

                agg.Occurrences++;
                agg.Sessions.Add(r.SessionId);
                if (r.AnalyzedAt < agg.FirstSeen) agg.FirstSeen = r.AnalyzedAt;
                if (r.AnalyzedAt > agg.LastSeen) agg.LastSeen = r.AnalyzedAt;

                // Severity totals (unfiltered — count all for the breakdown)
                switch (v.Severity)
                {
                    case ComplianceSeverity.Info: totalInfo++; break;
                    case ComplianceSeverity.Warning: totalWarning++; break;
                    case ComplianceSeverity.Critical: totalCritical++; break;
                }
            }

            if (sessionHasViolation)
                sessionsWithViolations.Add(r.SessionId);
        }

        var rules = ruleAggregates
            .OrderByDescending(kv => kv.Value.Occurrences)
            .Select(kv => new ComplianceRuleSummaryDto(
                RuleId: kv.Key.RuleId,
                RuleName: kv.Value.RuleName,
                Severity: kv.Key.Severity.ToString(),
                Occurrences: kv.Value.Occurrences,
                SessionsAffected: kv.Value.Sessions.Count,
                FirstSeen: kv.Value.FirstSeen,
                LastSeen: kv.Value.LastSeen))
            .ToArray();

        // When a severity filter is active, totals reflect only the filtered violations
        int filteredInfo = severityFilter == ComplianceSeverity.Info ? totalInfo : (severityFilter is null ? totalInfo : 0);
        int filteredWarning = severityFilter == ComplianceSeverity.Warning ? totalWarning : (severityFilter is null ? totalWarning : 0);
        int filteredCritical = severityFilter == ComplianceSeverity.Critical ? totalCritical : (severityFilter is null ? totalCritical : 0);
        int totalViolations = rules.Sum(r => r.Occurrences);

        return TypedResults.Ok(new ComplianceSummaryResponse(
            Rules: rules,
            TotalViolations: totalViolations,
            TotalSessionsWithViolations: sessionsWithViolations.Count,
            SeverityBreakdown: new ComplianceSeverityBreakdownDto(filteredInfo, filteredWarning, filteredCritical),
            From: fromDate,
            To: toDate));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static DateTimeOffset TruncateToBucket(DateTimeOffset dt, string bucket)
    {
        if (string.Equals(bucket, "week", StringComparison.OrdinalIgnoreCase))
        {
            // ISO week: Monday as first day
            var dow = (int)dt.DayOfWeek;
            var daysToMonday = dow == 0 ? 6 : dow - 1;
            var monday = dt.Date.AddDays(-daysToMonday);
            return new DateTimeOffset(monday, dt.Offset);
        }

        // Default: day
        return new DateTimeOffset(dt.Date, dt.Offset);
    }

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is Verbara.Platform.Core.TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }

    // ─── Private aggregation state (not DTOs) ────────────────────────────────

    private sealed class SentimentBucketAggregate
    {
        public int TotalCount { get; set; }
        public double SentimentScoreSum { get; set; }
        public int SentimentCount { get; set; }
        public int PositiveCount { get; set; }
        public int NeutralCount { get; set; }
        public int NegativeCount { get; set; }
    }

    private sealed class ComplianceRuleAggregate
    {
        public required string RuleName { get; init; }
        public int Occurrences { get; set; }
        public HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }
}

// ─── Call Analytics DTOs ──────────────────────────────────────────────────────

internal sealed record TopicTrendDto(
    string Topic,
    int Occurrences,
    double AvgConfidence);

internal sealed record TopicTrendsResponse(
    TopicTrendDto[] Trends,
    int TotalAnalyzed);

internal sealed record SentimentTrendPointDto(
    DateTimeOffset BucketStart,
    double? AvgSentimentScore,
    int PositiveCount,
    int NeutralCount,
    int NegativeCount,
    int TotalCount);

internal sealed record SentimentTrendsResponse(
    IReadOnlyList<SentimentTrendPointDto> Points,
    string Bucket,
    DateTimeOffset From,
    DateTimeOffset To);

internal sealed record ComplianceRuleSummaryDto(
    string RuleId,
    string RuleName,
    string Severity,
    int Occurrences,
    int SessionsAffected,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

internal sealed record ComplianceSeverityBreakdownDto(
    int Info,
    int Warning,
    int Critical);

internal sealed record ComplianceSummaryResponse(
    IReadOnlyList<ComplianceRuleSummaryDto> Rules,
    int TotalViolations,
    int TotalSessionsWithViolations,
    ComplianceSeverityBreakdownDto SeverityBreakdown,
    DateTimeOffset From,
    DateTimeOffset To);
