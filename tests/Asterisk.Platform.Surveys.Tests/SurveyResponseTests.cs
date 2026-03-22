using Asterisk.Platform.Core;
using Asterisk.Platform.Surveys;

namespace Asterisk.Platform.Surveys.Tests;

public class SurveyResponseTests
{
    [Fact]
    public void SurveyResponse_ShouldCreate_WhenValidInput()
    {
        var qId = EntityId.New();
        var response = new SurveyResponse
        {
            ResponseId = EntityId.New(),
            SurveyId = EntityId.New(),
            TenantId = new TenantId("t1"),
            ConversationId = EntityId.New(),
            ContactId = EntityId.New(),
            Answers = [new SurveyAnswer(qId, "4")],
            SubmittedAt = DateTimeOffset.UtcNow,
        };

        response.AgentId.Should().BeNull();
        response.Answers.Should().HaveCount(1);
        response.Answers[0].Value.Should().Be("4");
    }

    [Fact]
    public void SurveyResponse_ShouldAcceptAgentId()
    {
        var agentId = EntityId.New();
        var response = new SurveyResponse
        {
            ResponseId = EntityId.New(),
            SurveyId = EntityId.New(),
            TenantId = new TenantId("t1"),
            ConversationId = EntityId.New(),
            ContactId = EntityId.New(),
            AgentId = agentId,
            Answers = [new SurveyAnswer(EntityId.New(), "5")],
            SubmittedAt = DateTimeOffset.UtcNow,
        };

        response.AgentId.Should().Be(agentId);
    }

    [Fact]
    public void SurveyAnswer_ShouldCarryQuestionIdAndValue()
    {
        var qId = EntityId.From("q-001");
        var answer = new SurveyAnswer(qId, "Great service");

        answer.QuestionId.Value.Should().Be("q-001");
        answer.Value.Should().Be("Great service");
    }

    [Fact]
    public void SurveyResponse_ShouldAcceptMultipleAnswers()
    {
        var qId1 = EntityId.New();
        var qId2 = EntityId.New();
        var response = new SurveyResponse
        {
            ResponseId = EntityId.New(),
            SurveyId = EntityId.New(),
            TenantId = new TenantId("t1"),
            ConversationId = EntityId.New(),
            ContactId = EntityId.New(),
            Answers =
            [
                new SurveyAnswer(qId1, "5"),
                new SurveyAnswer(qId2, "Very satisfied!"),
            ],
            SubmittedAt = DateTimeOffset.UtcNow,
        };

        response.Answers.Should().HaveCount(2);
    }
}
