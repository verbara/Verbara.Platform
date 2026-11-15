using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Services.QaAvailability;
using Verbara.Sdk.Pro.CallAnalytics.Domain;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.QaAvailability;

/// <summary>
/// The QA data routes (<c>/analytics/qa</c>, <c>/analytics/qa/{sessionId}</c> and the three <c>/call-analytics/*</c>
/// routes) require the <see cref="LicenseFeature.CallAnalytics"/> module. The availability read and the CDR routes stay
/// on <see cref="LicenseFeature.Analytics"/>; without call analytics the CDR routes report no QA score, QA summary,
/// transcript or sentiment, on the list and on the detail.
/// </summary>
public sealed class CallAnalyticsLicenceGateEndpointTests
{
    private const string AnalysedSession = "analysed-positive";

    public static TheoryData<string> QaDataRoutes => new()
    {
        "/api/v1/analytics/qa",
        $"/api/v1/analytics/qa/{AnalysedSession}",
        "/api/v1/call-analytics/topics/trends",
        "/api/v1/call-analytics/sentiment/trends",
        "/api/v1/call-analytics/compliance/summary",
    };

    [Theory]
    [MemberData(nameof(QaDataRoutes))]
    public async Task QaDataRoute_ShouldAnswer402_WhenTheLicenceLacksCallAnalytics(string route)
    {
        await using var factory = new WithoutCallAnalyticsLicenceFactory();
        using var client = factory.CreateAuthenticatedClient();
        await SeedAnalysedCallAsync(factory);

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("CallAnalytics");
    }

    [Theory]
    [MemberData(nameof(QaDataRoutes))]
    public async Task QaDataRoute_ShouldAnswer200_WhenTheLicenceHasCallAnalytics(string route)
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await SeedAnalysedCallAsync(factory);

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(QaDataRoutes))]
    public async Task QaDataRoute_ShouldAnswer402_WhenTheLicenceHasCallAnalyticsButNotAnalytics(string route)
    {
        await using var factory = new CallAnalyticsWithoutAnalyticsLicenceFactory();
        using var client = factory.CreateAuthenticatedClient();
        await SeedAnalysedCallAsync(factory);

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetQaAvailability_ShouldAnswerNotLicensed_WhenTheLicenceHasAnalyticsWithoutCallAnalytics()
    {
        await using var factory = new WithoutCallAnalyticsLicenceFactory();
        await using var registered = RegisteredAnalysisWithoutCallAnalytics(factory);
        using var client = Authenticated(registered);

        var response = await client.GetAsync("/api/v1/analytics/qa-availability");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!.AsObject();
        json["available"]!.GetValue<bool>().Should().BeFalse();
        json["reason"]!.GetValue<string>().Should().Be("not_licensed");
    }

    [Fact]
    public async Task ListCdr_ShouldReportNoQaScoreOrSentiment_WhenTheLicenceLacksCallAnalytics()
    {
        await using var factory = new WithoutCallAnalyticsLicenceFactory();
        using var client = factory.CreateAuthenticatedClient();
        await SeedAnalysedCallAsync(factory);

        var response = await client.GetAsync("/api/v1/analytics/cdr");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var row = FindRow(JsonNode.Parse(body)!["data"]!.AsArray());
        row["hasQaScore"]!.GetValue<bool>().Should().BeFalse();
        row["qaScore"].Should().BeNull();
        row["sentimentLabel"].Should().BeNull();
    }

    [Fact]
    public async Task GetCdrDetail_ShouldReportNoQaScoreSummaryTranscriptOrSentiment_WhenTheLicenceLacksCallAnalytics()
    {
        await using var factory = new WithoutCallAnalyticsLicenceFactory();
        using var client = factory.CreateAuthenticatedClient();
        await SeedAnalysedCallAsync(factory);

        var response = await client.GetAsync($"/api/v1/analytics/cdr/{AnalysedSession}");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!;
        var row = json["cdr"]!;
        row["hasQaScore"]!.GetValue<bool>().Should().BeFalse();
        row["qaScore"].Should().BeNull();
        row["sentimentLabel"].Should().BeNull();
        json["qaSummary"].Should().BeNull();
        json["hasTranscript"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ListCdr_ShouldReportQaScoreAndSentiment_WhenTheLicenceHasCallAnalytics()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await SeedAnalysedCallAsync(factory);

        var body = await (await client.GetAsync("/api/v1/analytics/cdr")).Content.ReadAsStringAsync();

        var row = FindRow(JsonNode.Parse(body)!["data"]!.AsArray());
        row["hasQaScore"]!.GetValue<bool>().Should().BeTrue();
        row["qaScore"]!.GetValue<double>().Should().Be(80);
        row["sentimentLabel"]!.GetValue<string>().Should().Be("Positive");
    }

    [Fact]
    public async Task GetCdrDetail_ShouldReportQaScoreSummaryAndSentiment_WhenTheLicenceHasCallAnalytics()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await SeedAnalysedCallAsync(factory);

        var body = await (await client.GetAsync($"/api/v1/analytics/cdr/{AnalysedSession}")).Content.ReadAsStringAsync();

        var json = JsonNode.Parse(body)!;
        json["cdr"]!["hasQaScore"]!.GetValue<bool>().Should().BeTrue();
        json["cdr"]!["sentimentLabel"]!.GetValue<string>().Should().Be("Positive");
        json["qaSummary"]!["narrative"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    private static JsonNode FindRow(JsonArray rows) =>
        rows.Single(r => r!["sessionId"]!.GetValue<string>() == AnalysedSession)!;

    private static async Task SeedAnalysedCallAsync(UnifiedPlatformApiFactory factory)
    {
        await factory.CdrStore.UpsertAsync(MakeCdr(AnalysedSession));
        await factory.QaStore.SaveAsync(MakeAnalysis(AnalysedSession));
    }

    private static WebApplicationFactory<Program> RegisteredAnalysisWithoutCallAnalytics(UnifiedPlatformApiFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var guard = Substitute.For<ILicenseGuard>();
            var allowed = new LicenseGuardResult(true, null);
            var denied = new LicenseGuardResult(false, LicenseBlockReason.NotLicensed);
            guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(allowed);
            guard.CanExecute(LicenseFeature.CallAnalytics).Returns(denied);
            guard.CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>()).Returns(allowed);
            guard.CanExecuteAsync(LicenseFeature.CallAnalytics, Arg.Any<CancellationToken>()).Returns(denied);
            services.AddSingleton(guard);

            foreach (var d in services.Where(d => d.ServiceType == typeof(QaAvailabilityService)).ToList())
                services.Remove(d);
            services.AddSingleton(sp => new QaAvailabilityService(
                analysisRegistered: true,
                sp.GetRequiredService<IQaAnalysisSetup>(),
                sp.GetRequiredService<ILicenseGuard>()));
        }));

    private static HttpClient Authenticated(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {UnifiedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", UnifiedPlatformApiFactory.TestTenantId);
        return client;
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

    private static CallAnalysisResult MakeAnalysis(string sessionId) => new()
    {
        SessionId = sessionId,
        TenantId = UnifiedPlatformApiFactory.TestTenantId,
        AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        ProcessingTime = TimeSpan.FromSeconds(2),
        QualityScore = new QaResult { TotalScore = 8, MaxPossibleScore = 10, Items = [] },
        Sentiment = new SentimentResult
        {
            OverallScore = 0.7f,
            OverallLabel = SentimentLabel.Positive,
            StartLabel = SentimentLabel.Neutral,
            EndLabel = SentimentLabel.Positive,
            Trend = SentimentTrend.Improving,
        },
        Summary = new CallSummary
        {
            Reason = "Billing inquiry",
            Outcome = "Resolved",
            Narrative = "Customer called about a billing issue which was resolved.",
        },
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

    private sealed class WithoutCallAnalyticsLicenceFactory : UnifiedPlatformApiFactory
    {
        protected override LicenseFeature LicensedFeatures => LicenseFeature.All & ~LicenseFeature.CallAnalytics;
    }

    private sealed class CallAnalyticsWithoutAnalyticsLicenceFactory : UnifiedPlatformApiFactory
    {
        protected override LicenseFeature LicensedFeatures => LicenseFeature.All & ~LicenseFeature.Analytics;
    }
}
