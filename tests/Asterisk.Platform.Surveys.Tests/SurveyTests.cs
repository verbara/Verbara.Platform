using Asterisk.Platform.Core;
using Asterisk.Platform.Surveys;

namespace Asterisk.Platform.Surveys.Tests;

public class SurveyTests
{
    [Fact]
    public void Survey_ShouldCreate_WhenValidInput()
    {
        var survey = MakeCsatSurvey();

        survey.SurveyId.Value.Should().NotBeNullOrEmpty();
        survey.Name.Should().Be("Post-Chat CSAT");
        survey.Type.Should().Be(SurveyType.Csat);
        survey.IsActive.Should().BeTrue();
        survey.Questions.Should().HaveCount(1);
    }

    [Fact]
    public void Survey_ShouldAllowDeactivation()
    {
        var survey = MakeCsatSurvey();
        survey.IsActive = false;

        survey.IsActive.Should().BeFalse();
    }

    [Fact]
    public void SurveyQuestion_ScaleType_ShouldHaveNoOptions()
    {
        var q = new SurveyQuestion
        {
            QuestionId = EntityId.New(),
            Text = "Rate your experience (1-5)",
            Type = SurveyQuestionType.Scale,
        };

        q.Type.Should().Be(SurveyQuestionType.Scale);
        q.Options.Should().BeNull();
    }

    [Fact]
    public void SurveyQuestion_ChoiceType_ShouldHaveOptions()
    {
        var q = new SurveyQuestion
        {
            QuestionId = EntityId.New(),
            Text = "What brought you here?",
            Type = SurveyQuestionType.Choice,
            Options = ["Billing", "Technical", "General"],
        };

        q.Options.Should().HaveCount(3);
        q.Options![0].Should().Be("Billing");
    }

    [Fact]
    public void SurveyQuestion_FreeTextType_ShouldHaveNoOptions()
    {
        var q = new SurveyQuestion
        {
            QuestionId = EntityId.New(),
            Text = "Any additional comments?",
            Type = SurveyQuestionType.FreeText,
        };

        q.Type.Should().Be(SurveyQuestionType.FreeText);
        q.Options.Should().BeNull();
    }

    [Fact]
    public void NpsSurvey_ShouldCreate_WithCorrectType()
    {
        var survey = MakeNpsSurvey();

        survey.Type.Should().Be(SurveyType.Nps);
        survey.Questions[0].Type.Should().Be(SurveyQuestionType.Scale);
    }

    // -------------------------------------------------------------------------

    private static Survey MakeCsatSurvey() =>
        new()
        {
            SurveyId = EntityId.New(),
            TenantId = new TenantId("t1"),
            Name = "Post-Chat CSAT",
            Type = SurveyType.Csat,
            Questions =
            [
                new SurveyQuestion
                {
                    QuestionId = EntityId.New(),
                    Text = "How would you rate this interaction?",
                    Type = SurveyQuestionType.Scale,
                },
            ],
        };

    private static Survey MakeNpsSurvey() =>
        new()
        {
            SurveyId = EntityId.New(),
            TenantId = new TenantId("t1"),
            Name = "NPS Survey",
            Type = SurveyType.Nps,
            Questions =
            [
                new SurveyQuestion
                {
                    QuestionId = EntityId.New(),
                    Text = "How likely are you to recommend us? (0-10)",
                    Type = SurveyQuestionType.Scale,
                },
            ],
        };
}
