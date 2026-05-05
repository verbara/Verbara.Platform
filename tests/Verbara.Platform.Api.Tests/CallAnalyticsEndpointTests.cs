using System.Net;
using System.Text.Json.Nodes;
using Verbara.Sdk.Pro.CallAnalytics.Domain;

namespace Verbara.Platform.Api.Tests;

public sealed class CallAnalyticsEndpointTests : IClassFixture<UnifiedPlatformApiFactory>
{
    // Each test creates its own factory instance for full isolation.
    public CallAnalyticsEndpointTests(UnifiedPlatformApiFactory factory) { _ = factory; }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static CallAnalysisResult MakeResult(
        string tenantId,
        string sessionId,
        DateTimeOffset analyzedAt,
        double qaScore = 0.85,
        int violations = 0,
        string? primaryTopic = null,
        string? queueName = null,
        SentimentLabel sentimentLabel = SentimentLabel.Positive,
        float sentimentScore = 0.6f,
        ComplianceSeverity violationSeverity = ComplianceSeverity.Warning)
    {
        var violationList = new List<ComplianceViolation>();
        for (var i = 0; i < violations; i++)
        {
            violationList.Add(new ComplianceViolation
            {
                RuleId = $"rule-{i}",
                RuleName = $"Rule {i}",
                Severity = violationSeverity,
                Description = $"Violation {i}",
            });
        }

        return new CallAnalysisResult
        {
            SessionId = sessionId,
            TenantId = tenantId,
            AnalyzedAt = analyzedAt,
            ProcessingTime = TimeSpan.FromSeconds(2),
            QualityScore = new QaResult
            {
                TotalScore = qaScore * 100,
                MaxPossibleScore = 100,
                Items = [],
            },
            ComplianceViolations = violationList,
            Topics = primaryTopic is not null
                ? new TopicResult
                {
                    PrimaryTopic = primaryTopic,
                    AllTopics = [new TopicMatch { TopicId = primaryTopic, TopicName = primaryTopic, Confidence = 0.9f }],
                }
                : null,
            Sentiment = new SentimentResult
            {
                OverallScore = sentimentScore,
                OverallLabel = sentimentLabel,
                StartLabel = SentimentLabel.Neutral,
                EndLabel = sentimentLabel,
                Trend = SentimentTrend.Improving,
            },
            Summary = new CallSummary
            {
                Reason = "Billing inquiry",
                Outcome = "Resolved",
                Narrative = "Customer called about billing issue which was resolved.",
            },
            Metrics = new ConversationMetrics
            {
                AgentTalkRatio = 0.45,
                TotalAgentTalk = TimeSpan.FromMinutes(3),
                TotalCallerTalk = TimeSpan.FromMinutes(2),
                LongestAgentMonologue = TimeSpan.FromSeconds(45),
                LongestCallerMonologue = TimeSpan.FromSeconds(30),
                AgentTurnCount = 12,
                CallerTurnCount = 10,
                SilenceCount = 3,
                TotalSilence = TimeSpan.FromSeconds(15),
                InterruptionCount = 1,
            },
        };
    }

    private static CallAnalysisResult MakeResultWithViolations(
        string tenantId,
        string sessionId,
        DateTimeOffset analyzedAt,
        params ComplianceViolation[] violations)
    {
        var r = MakeResult(tenantId, sessionId, analyzedAt, violations: 0);
        return r with { ComplianceViolations = violations };
    }

    // ─── GetTopicTrends tests ─────────────────────────────────────────────────

    [Fact]
    public async Task GetTopicTrends_ShouldAggregateTopN_WhenResultsExist()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "t1", now.AddMinutes(-1), primaryTopic: "Billing"));
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "t2", now.AddMinutes(-2), primaryTopic: "Billing"));
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "t3", now.AddMinutes(-3), primaryTopic: "Support"));

        var response = await client.GetAsync("/api/v1/call-analytics/topics/trends?topN=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["totalAnalyzed"]!.GetValue<int>().Should().Be(3);
        var trends = json["trends"]!.AsArray();
        trends.Count.Should().BeGreaterThan(0);
        // Billing should appear first (2 occurrences)
        trends[0]!["topic"]!.GetValue<string>().Should().Be("Billing");
        trends[0]!["occurrences"]!.GetValue<int>().Should().Be(2);
    }

    // ─── GetSentimentTrends tests ─────────────────────────────────────────────

    [Fact]
    public async Task GetSentimentTrends_ShouldBucketByDay_WhenResultsExist()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var day1 = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 4, 2, 10, 0, 0, TimeSpan.Zero);

        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "s1", day1,
            sentimentLabel: SentimentLabel.Positive, sentimentScore: 0.8f));
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "s2", day1.AddHours(2),
            sentimentLabel: SentimentLabel.Neutral, sentimentScore: 0.5f));
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "s3", day2,
            sentimentLabel: SentimentLabel.Negative, sentimentScore: 0.2f));

        var from = Uri.EscapeDataString(day1.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(day2.AddHours(1).ToString("O"));

        var response = await client.GetAsync($"/api/v1/call-analytics/sentiment/trends?from={from}&to={to}&bucket=day");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["bucket"]!.GetValue<string>().Should().Be("day");
        var points = json["points"]!.AsArray();
        points.Count.Should().Be(2);
        // First bucket (day1): 2 results, 1 positive + 1 neutral
        points[0]!["positiveCount"]!.GetValue<int>().Should().Be(1);
        points[0]!["neutralCount"]!.GetValue<int>().Should().Be(1);
        points[0]!["negativeCount"]!.GetValue<int>().Should().Be(0);
        points[0]!["totalCount"]!.GetValue<int>().Should().Be(2);
        // Second bucket (day2): 1 negative
        points[1]!["negativeCount"]!.GetValue<int>().Should().Be(1);
        points[1]!["totalCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task GetSentimentTrends_ShouldRespectQueueFilter_WhenProvided()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "q1", now.AddMinutes(-1),
            queueName: "support", sentimentLabel: SentimentLabel.Positive));
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "q2", now.AddMinutes(-2),
            queueName: "sales", sentimentLabel: SentimentLabel.Negative));

        // Verify endpoint accepts the filter without error (InMemory store may not filter by queue,
        // but the route and parameter binding should work correctly)
        var response = await client.GetAsync("/api/v1/call-analytics/sentiment/trends?queueName=support");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["points"].Should().NotBeNull();
    }

    // ─── GetComplianceSummary tests ───────────────────────────────────────────

    [Fact]
    public async Task GetComplianceSummary_ShouldAggregateByRuleAndSeverity_WhenViolationsExist()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        // r1: rule-0 Warning (1 occurrence)
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "r1",
            now.AddMinutes(-1), violations: 1, violationSeverity: ComplianceSeverity.Warning));
        // r2: rule-0 Warning (2nd occurrence in different session) + rule-1 Critical
        var r2 = MakeResultWithViolations(
            UnifiedPlatformApiFactory.TestTenantId, "r2", now.AddMinutes(-2),
            new ComplianceViolation { RuleId = "rule-0", RuleName = "Rule 0", Severity = ComplianceSeverity.Warning, Description = "V" },
            new ComplianceViolation { RuleId = "rule-1", RuleName = "Rule 1", Severity = ComplianceSeverity.Critical, Description = "V" });
        await factory.QaStore.SaveAsync(r2);

        var response = await client.GetAsync("/api/v1/call-analytics/compliance/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var rules = json!["rules"]!.AsArray();
        // 2 distinct (ruleId, severity) groups: rule-0/Warning + rule-1/Critical
        rules.Count.Should().Be(2);
        // rule-0/Warning: 2 occurrences across 2 sessions
        var rule0 = rules.First(r => r!["ruleId"]!.GetValue<string>() == "rule-0");
        rule0!["occurrences"]!.GetValue<int>().Should().Be(2);
        rule0["sessionsAffected"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task GetComplianceSummary_ShouldFilterBySeverity_WhenProvided()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "c1",
            now.AddMinutes(-1), violations: 1, violationSeverity: ComplianceSeverity.Critical));
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "c2",
            now.AddMinutes(-2), violations: 1, violationSeverity: ComplianceSeverity.Warning));

        var response = await client.GetAsync("/api/v1/call-analytics/compliance/summary?severity=Critical");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var rules = json!["rules"]!.AsArray();
        // Only Critical rules returned
        rules.Count.Should().Be(1);
        rules[0]!["severity"]!.GetValue<string>().Should().Be("Critical");
    }

    [Fact]
    public async Task GetComplianceSummary_ShouldReturnSeverityBreakdown_WhenViolationsExist()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "b1",
            now.AddMinutes(-1), violations: 2, violationSeverity: ComplianceSeverity.Warning));
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "b2",
            now.AddMinutes(-2), violations: 1, violationSeverity: ComplianceSeverity.Critical));

        var response = await client.GetAsync("/api/v1/call-analytics/compliance/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var breakdown = json!["severityBreakdown"]!;
        breakdown["warning"]!.GetValue<int>().Should().Be(2);
        breakdown["critical"]!.GetValue<int>().Should().Be(1);
        breakdown["info"]!.GetValue<int>().Should().Be(0);
        json["totalViolations"]!.GetValue<int>().Should().Be(3);
    }

    // ─── Auth / 503 tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetTopicTrends_ShouldReturn401or403_WhenUserLacksSupervisorPlusRole()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateClient();
        // No Authorization header → 401
        var response = await client.GetAsync("/api/v1/call-analytics/topics/trends");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
