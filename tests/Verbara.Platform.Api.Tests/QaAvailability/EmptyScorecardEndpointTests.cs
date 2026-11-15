using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Verbara.Sdk.Pro.CallAnalytics.Domain;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.Tests.QaAvailability;

/// <summary>
/// An analysis made with an empty scorecard (no item, so a maximum score of 0) reports no QA score: the CDR list and
/// detail answer <c>hasQaScore</c> false with <c>qaScore</c> null, the CDR detail has no QA summary, the QA list leaves
/// it out and the QA detail answers 404. Its sentiment stays readable on the CDR surfaces.
/// </summary>
public sealed class EmptyScorecardEndpointTests
{
    [Fact]
    public async Task GetCdrDetail_ShouldReportNoQaScore_WhenScorecardWasEmpty()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(MakeCdr("empty-detail"));
        await factory.QaStore.SaveAsync(MakeAnalysis("empty-detail", total: 0, max: 0, SentimentLabel.Positive));

        var response = await client.GetAsync("/api/v1/analytics/cdr/empty-detail");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["cdr"]!["hasQaScore"]!.GetValue<bool>().Should().BeFalse();
        json["cdr"]!["qaScore"].Should().BeNull();
        json["cdr"]!["sentimentLabel"]!.GetValue<string>().Should().Be("Positive");
    }

    [Fact]
    public async Task GetCdrDetail_ShouldAnswerNoQaSummary_WhenScorecardWasEmpty()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(MakeCdr("empty-summary"));
        await factory.QaStore.SaveAsync(MakeAnalysis("empty-summary", total: 0, max: 0, SentimentLabel.Positive));

        var response = await client.GetAsync("/api/v1/analytics/cdr/empty-summary");

        // The QA detail of the same call answers 404, so the CDR detail offers no QA summary to open.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["qaSummary"].Should().BeNull();
        (await client.GetAsync("/api/v1/analytics/qa/empty-summary")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCdrDetail_ShouldReportQaScore_WhenScorecardHadItems()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(MakeCdr("scored-detail"));
        await factory.QaStore.SaveAsync(MakeAnalysis("scored-detail", total: 5, max: 10));

        var json = JsonNode.Parse(
            await (await client.GetAsync("/api/v1/analytics/cdr/scored-detail")).Content.ReadAsStringAsync())!;

        json["cdr"]!["hasQaScore"]!.GetValue<bool>().Should().BeTrue();
        json["cdr"]!["qaScore"]!.GetValue<double>().Should().BeApproximately(50.0, 0.001);
        json["qaSummary"]!["qaScore"]!.GetValue<double>().Should().BeApproximately(50.0, 0.001);
    }

    [Fact]
    public async Task ListCdr_ShouldReportNoQaScore_WhenScorecardWasEmpty()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(MakeCdr("empty-row"));
        await factory.CdrStore.UpsertAsync(MakeCdr("scored-row"));
        await factory.QaStore.SaveAsync(MakeAnalysis("empty-row", total: 0, max: 0, SentimentLabel.Negative));
        await factory.QaStore.SaveAsync(MakeAnalysis("scored-row", total: 5, max: 10));

        var response = await client.GetAsync("/api/v1/analytics/cdr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!.AsArray()
            .ToDictionary(r => r!["sessionId"]!.GetValue<string>(), r => r!);
        rows["empty-row"]["hasQaScore"]!.GetValue<bool>().Should().BeFalse();
        rows["empty-row"]["qaScore"].Should().BeNull();
        rows["empty-row"]["sentimentLabel"]!.GetValue<string>().Should().Be("Negative");
        rows["scored-row"]["hasQaScore"]!.GetValue<bool>().Should().BeTrue();
        rows["scored-row"]["qaScore"]!.GetValue<double>().Should().BeApproximately(50.0, 0.001);
    }

    [Fact]
    public async Task ListQa_ShouldLeaveOutAnalysis_WhenScorecardWasEmpty()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.QaStore.SaveAsync(MakeAnalysis("empty-qa", total: 0, max: 0));
        await factory.QaStore.SaveAsync(MakeAnalysis("scored-qa", total: 5, max: 10));

        var response = await client.GetAsync("/api/v1/analytics/qa");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!.AsArray();
        rows.Select(r => r!["sessionId"]!.GetValue<string>()).Should().Equal("scored-qa");
        rows[0]!["qaScore"]!.GetValue<double>().Should().BeApproximately(50.0, 0.001);
    }

    [Fact]
    public async Task GetQaDetail_ShouldReturn404_WhenScorecardWasEmpty()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.QaStore.SaveAsync(MakeAnalysis("empty-qa-detail", total: 0, max: 0));

        var response = await client.GetAsync("/api/v1/analytics/qa/empty-qa-detail");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0.01")]
    [InlineData("100.5")]
    [InlineData("1000")]
    [InlineData("NaN")]
    public async Task ListQa_ShouldReturn400_WhenMinScoreOutsideZeroToHundred(string minScore)
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/v1/analytics/qa?minScore={minScore}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["error"]!.GetValue<string>().Should().Contain("minScore");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(55)]
    [InlineData(100)]
    public async Task ListQa_ShouldReturn200_WhenMinScoreWithinZeroToHundred(double minScore)
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync(
            $"/api/v1/analytics/qa?minScore={minScore.ToString(CultureInfo.InvariantCulture)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static CompletedSessionRow MakeCdr(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        return new CompletedSessionRow
        {
            TenantId = UnifiedPlatformApiFactory.TestTenantId,
            SessionId = sessionId,
            ServerId = "server-1",
            Direction = 0,
            CallerIdNum = "+15551234567",
            QueueName = "support",
            AgentId = "agent-1",
            StartedAt = now.AddHours(-1),
            ConnectedAt = now.AddHours(-1).AddSeconds(10),
            CompletedAt = now.AddHours(-1).AddMinutes(5),
            DurationMs = 300_000,
            TalkTimeMs = 280_000,
            WaitTimeMs = 10_000,
            HoldTimeMs = 0,
            HangupCause = 16,
            FinalState = 8,
            CreatedAt = now.AddHours(-1),
        };
    }

    private static CallAnalysisResult MakeAnalysis(
        string sessionId, double total, double max, SentimentLabel? sentiment = null) => new()
    {
        SessionId = sessionId,
        TenantId = UnifiedPlatformApiFactory.TestTenantId,
        AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        ProcessingTime = TimeSpan.FromSeconds(2),
        QualityScore = new QaResult { TotalScore = total, MaxPossibleScore = max, Items = [] },
        Sentiment = sentiment is { } label
            ? new SentimentResult
            {
                OverallScore = 0.5f,
                OverallLabel = label,
                StartLabel = label,
                EndLabel = label,
                Trend = SentimentTrend.Stable,
            }
            : null,
        Metrics = new ConversationMetrics
        {
            AgentTalkRatio = 0.6,
            TotalAgentTalk = TimeSpan.FromSeconds(180),
            TotalCallerTalk = TimeSpan.FromSeconds(120),
            LongestAgentMonologue = TimeSpan.FromSeconds(60),
            LongestCallerMonologue = TimeSpan.FromSeconds(45),
            AgentTurnCount = 12,
            CallerTurnCount = 10,
            SilenceCount = 0,
            TotalSilence = TimeSpan.Zero,
            InterruptionCount = 0,
        },
    };
}
