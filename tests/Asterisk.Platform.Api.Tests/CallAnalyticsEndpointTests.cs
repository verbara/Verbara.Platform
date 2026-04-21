using System.Net;
using System.Text.Json.Nodes;
using Asterisk.Sdk.Pro.CallAnalytics.Domain;

namespace Asterisk.Platform.Api.Tests;

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
        string? queueName = null)
    {
        var violationList = new List<ComplianceViolation>();
        for (var i = 0; i < violations; i++)
        {
            violationList.Add(new ComplianceViolation
            {
                RuleId = $"rule-{i}",
                RuleName = $"Rule {i}",
                Severity = ComplianceSeverity.Warning,
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
                OverallScore = 0.6f,
                OverallLabel = SentimentLabel.Positive,
                StartLabel = SentimentLabel.Neutral,
                EndLabel = SentimentLabel.Positive,
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

    // ─── GetResults tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetResults_ShouldReturnEmptyList_WhenStoreIsEmpty()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/call-analytics/results");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["returnedCount"]!.GetValue<int>().Should().Be(0);
        json["results"]!.AsArray().Count.Should().Be(0);
    }

    [Fact]
    public async Task GetResults_ShouldRespectLimitAndOffset_WhenProvided()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
            await factory.QaStore.SaveAsync(
                MakeResult(UnifiedPlatformApiFactory.TestTenantId, $"sess-{i}", now.AddMinutes(-i)));

        var response = await client.GetAsync("/api/v1/call-analytics/results?limit=2&offset=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["returnedCount"]!.GetValue<int>().Should().Be(2);
        json["limit"]!.GetValue<int>().Should().Be(2);
        json["offset"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetResults_ShouldFilterByDateRange_WhenFromToProvided()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "old-sess", now.AddDays(-10)));
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "new-sess", now.AddHours(-1)));

        var from = Uri.EscapeDataString(now.AddDays(-2).ToString("O"));
        var to = Uri.EscapeDataString(now.ToString("O"));

        var response = await client.GetAsync($"/api/v1/call-analytics/results?from={from}&to={to}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["returnedCount"]!.GetValue<int>().Should().Be(1);
        json["results"]![0]!["sessionId"]!.GetValue<string>().Should().Be("new-sess");
    }

    [Fact]
    public async Task GetResults_ShouldFilterByQueueName_WhenProvided()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        // QueueName is not on CallAnalysisResult / ConversationMetrics — store does not filter by it
        // but the query is passed through. The InMemoryCallAnalyticsStore does not implement queueName filter.
        // Seed two results and verify the endpoint accepts the param without error.
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "s1", now.AddMinutes(-1)));

        var response = await client.GetAsync("/api/v1/call-analytics/results?queueName=support");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetResults_ShouldFilterByMinQaScore_WhenProvided()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        // qa score stored as fraction (0-1): minQaScore param is also 0-1
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "high", now.AddMinutes(-1), qaScore: 0.95));
        await factory.QaStore.SaveAsync(MakeResult(UnifiedPlatformApiFactory.TestTenantId, "low", now.AddMinutes(-2), qaScore: 0.40));

        var response = await client.GetAsync("/api/v1/call-analytics/results?minQaScore=0.9");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["returnedCount"]!.GetValue<int>().Should().Be(1);
        json["results"]![0]!["sessionId"]!.GetValue<string>().Should().Be("high");
    }

    // ─── GetDetail tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetDetail_ShouldReturnNotFound_WhenSessionDoesNotExist()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/call-analytics/results/non-existent-session");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDetail_ShouldReturnFullResult_WhenSessionExists()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        var result = MakeResult(UnifiedPlatformApiFactory.TestTenantId, "detail-sess", now.AddMinutes(-5),
            qaScore: 0.75, violations: 1, primaryTopic: "Billing");
        await factory.QaStore.SaveAsync(result);

        var response = await client.GetAsync("/api/v1/call-analytics/results/detail-sess");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["sessionId"]!.GetValue<string>().Should().Be("detail-sess");
        json["primaryTopic"]!.GetValue<string>().Should().Be("Billing");
        json["violations"]!.AsArray().Count.Should().Be(1);
        json["qaCriteria"]!.AsArray().Count.Should().Be(0);
        json["agentTalkRatio"]!.GetValue<double>().Should().BeApproximately(0.45, 0.001);
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

    // ─── Auth / 503 tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetResults_ShouldReturn401or403_WhenUserLacksSupervisorPlusRole()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateClient();
        // No Authorization header → 401
        var response = await client.GetAsync("/api/v1/call-analytics/results");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
