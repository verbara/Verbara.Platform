using Asterisk.Platform.Core;
using Asterisk.Platform.Surveys;

namespace Asterisk.Platform.Surveys.Tests;

public class AgentAnalyticsTests
{
    private readonly ISurveyResponseStore _responseStore = Substitute.For<ISurveyResponseStore>();
    private readonly ISurveyStore _surveyStore = Substitute.For<ISurveyStore>();
    private readonly InMemorySurveyAnalytics _analytics;

    private readonly TenantId _tenant = new("t1");
    private readonly EntityId _surveyId = EntityId.From("survey-agent");
    private readonly EntityId _qId = EntityId.From("q1");
    private readonly EntityId _agent1 = EntityId.From("agent-001");
    private readonly EntityId _agent2 = EntityId.From("agent-002");

    public AgentAnalyticsTests()
    {
        _analytics = new InMemorySurveyAnalytics(_responseStore, _surveyStore);
        _surveyStore.GetByIdAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(MakeSurvey());
    }

    [Fact]
    public async Task GetByAgentAsync_ShouldFilterByAgentId()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                MakeResponse("5", _agent1),
                MakeResponse("4", _agent1),
                MakeResponse("2", _agent2),
            ]);

        var summary = await _analytics.GetByAgentAsync(_tenant, _surveyId, _agent1, CancellationToken.None);

        summary.TotalResponses.Should().Be(2);
        summary.AverageScore.Should().BeApproximately(4.5, 0.001);
    }

    [Fact]
    public async Task GetByAgentAsync_ShouldReturnZero_WhenNoResponsesForAgent()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns([MakeResponse("5", _agent2)]);

        var summary = await _analytics.GetByAgentAsync(_tenant, _surveyId, _agent1, CancellationToken.None);

        summary.TotalResponses.Should().Be(0);
        summary.AverageScore.Should().Be(0d);
    }

    [Fact]
    public async Task GetByAgentAsync_ShouldExcludeResponsesWithoutAgentId()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                MakeResponse("5", _agent1),
                new SurveyResponse
                {
                    ResponseId = EntityId.New(),
                    SurveyId = _surveyId,
                    TenantId = _tenant,
                    ConversationId = EntityId.New(),
                    ContactId = EntityId.New(),
                    Answers = [new SurveyAnswer(_qId, "3")],
                    SubmittedAt = DateTimeOffset.UtcNow,
                },
            ]);

        var summary = await _analytics.GetByAgentAsync(_tenant, _surveyId, _agent1, CancellationToken.None);

        summary.TotalResponses.Should().Be(1);
    }

    // -------------------------------------------------------------------------

    private Survey MakeSurvey() =>
        new()
        {
            SurveyId = _surveyId,
            TenantId = _tenant,
            Name = "CSAT",
            Type = SurveyType.Csat,
            Questions = [],
        };

    private SurveyResponse MakeResponse(string score, EntityId agentId) =>
        new()
        {
            ResponseId = EntityId.New(),
            SurveyId = _surveyId,
            TenantId = _tenant,
            ConversationId = EntityId.New(),
            ContactId = EntityId.New(),
            AgentId = agentId,
            Answers = [new SurveyAnswer(_qId, score)],
            SubmittedAt = DateTimeOffset.UtcNow,
        };
}
