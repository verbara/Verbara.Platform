using System.Net;
using System.Text.Json.Nodes;
using Verbara.Sdk.Pro.CallAnalytics.Domain;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.Tests.QaAvailability;

/// <summary>
/// The CDR detail never reports <c>hasTranscript</c> true: the API serves no transcript route, so an analysed call
/// with a stored summary still answers false.
/// </summary>
public sealed class CdrTranscriptFlagEndpointTests
{
    [Fact]
    public async Task GetCdrDetail_ShouldReportNoTranscript_WhenAnalysisHasSummary()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(MakeCdr("summarised"));
        await factory.QaStore.SaveAsync(MakeAnalysis("summarised"));

        var response = await client.GetAsync("/api/v1/analytics/cdr/summarised");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["qaSummary"]!["narrative"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        json["hasTranscript"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GetCdrDetail_ShouldReportNoTranscript_WhenCallWasNotAnalysed()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(MakeCdr("unanalysed"));

        var json = JsonNode.Parse(
            await (await client.GetAsync("/api/v1/analytics/cdr/unanalysed")).Content.ReadAsStringAsync())!;

        json["hasTranscript"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GetCdrTranscript_ShouldReturn404_WhenAnalysisHasSummary()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(MakeCdr("no-route"));
        await factory.QaStore.SaveAsync(MakeAnalysis("no-route"));

        var response = await client.GetAsync("/api/v1/analytics/cdr/no-route/transcript");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
}
