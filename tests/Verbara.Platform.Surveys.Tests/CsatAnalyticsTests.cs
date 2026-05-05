using Verbara.Platform.Core;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Surveys.Tests;

public class CsatAnalyticsTests
{
    private readonly ISurveyResponseStore _responseStore = Substitute.For<ISurveyResponseStore>();
    private readonly ISurveyStore _surveyStore = Substitute.For<ISurveyStore>();
    private readonly InMemorySurveyAnalytics _analytics;

    private readonly TenantId _tenant = new("t1");
    private readonly EntityId _surveyId = EntityId.From("survey-csat");

    public CsatAnalyticsTests()
    {
        _analytics = new InMemorySurveyAnalytics(_responseStore, _surveyStore);
        _surveyStore.GetByIdAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(MakeSurvey(SurveyType.Csat));
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldReturnZero_WhenNoResponses()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns([]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.TotalResponses.Should().Be(0);
        summary.AverageScore.Should().Be(0d);
        summary.NpsScore.Should().BeNull();
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldCalculateAverage_ForCsat()
    {
        var qId = EntityId.From("q1");
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                MakeResponse(qId, "4"),
                MakeResponse(qId, "5"),
                MakeResponse(qId, "3"),
            ]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.TotalResponses.Should().Be(3);
        summary.AverageScore.Should().BeApproximately(4.0, 0.001);
        summary.NpsScore.Should().BeNull();
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldIgnoreFreeTextAnswers_WhenCalculatingAverage()
    {
        var qScale = EntityId.From("q-scale");
        var qText = EntityId.From("q-text");
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new SurveyResponse
                {
                    ResponseId = EntityId.New(),
                    SurveyId = _surveyId,
                    TenantId = _tenant,
                    ConversationId = EntityId.New(),
                    ContactId = EntityId.New(),
                    Answers =
                    [
                        new SurveyAnswer(qText, "great service"),
                        new SurveyAnswer(qScale, "5"),
                    ],
                    SubmittedAt = DateTimeOffset.UtcNow,
                },
            ]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.AverageScore.Should().BeApproximately(5.0, 0.001);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldReturnZeroAverage_WhenOnlyFreeTextAnswers()
    {
        var qId = EntityId.From("q-text");
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns([MakeResponse(qId, "great!")]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.TotalResponses.Should().Be(1);
        summary.AverageScore.Should().Be(0d);
    }

    // -------------------------------------------------------------------------

    private Survey MakeSurvey(SurveyType type) =>
        new()
        {
            SurveyId = _surveyId,
            TenantId = _tenant,
            Name = "CSAT",
            Type = type,
            Questions = [],
        };

    private SurveyResponse MakeResponse(EntityId questionId, string value) =>
        new()
        {
            ResponseId = EntityId.New(),
            SurveyId = _surveyId,
            TenantId = _tenant,
            ConversationId = EntityId.New(),
            ContactId = EntityId.New(),
            Answers = [new SurveyAnswer(questionId, value)],
            SubmittedAt = DateTimeOffset.UtcNow,
        };
}
