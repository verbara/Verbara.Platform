using Verbara.Platform.Core;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Surveys.Tests;

public class NpsAnalyticsTests
{
    private readonly ISurveyResponseStore _responseStore = Substitute.For<ISurveyResponseStore>();
    private readonly ISurveyStore _surveyStore = Substitute.For<ISurveyStore>();
    private readonly InMemorySurveyAnalytics _analytics;

    private readonly TenantId _tenant = new("t1");
    private readonly EntityId _surveyId = EntityId.From("survey-nps");
    private readonly EntityId _qId = EntityId.From("q-nps");

    public NpsAnalyticsTests()
    {
        _analytics = new InMemorySurveyAnalytics(_responseStore, _surveyStore);
        _surveyStore.GetByIdAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(MakeSurvey());
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldComputeNps_WithMixedScores()
    {
        // promoters: 9,10 = 2 | passives: 7,8 = 2 | detractors: 0-6 = 2
        // NPS = (2-2)/6 * 100 = 0
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                MakeResponse("10"),
                MakeResponse("9"),
                MakeResponse("8"),
                MakeResponse("7"),
                MakeResponse("5"),
                MakeResponse("3"),
            ]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.TotalResponses.Should().Be(6);
        summary.Promoters.Should().Be(2);
        summary.Passives.Should().Be(2);
        summary.Detractors.Should().Be(2);
        summary.NpsScore.Should().BeApproximately(0d, 0.001);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldReturnPositiveNps_WhenAllPromoters()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                MakeResponse("10"),
                MakeResponse("10"),
                MakeResponse("9"),
            ]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.Promoters.Should().Be(3);
        summary.Detractors.Should().Be(0);
        summary.NpsScore.Should().BeApproximately(100d, 0.001);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldReturnNegativeNps_WhenAllDetractors()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                MakeResponse("2"),
                MakeResponse("4"),
                MakeResponse("6"),
            ]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.Promoters.Should().Be(0);
        summary.Detractors.Should().Be(3);
        summary.NpsScore.Should().BeApproximately(-100d, 0.001);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldClassifyBoundary_ScoreSix_AsDetractor()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns([MakeResponse("6")]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.Detractors.Should().Be(1);
        summary.Passives.Should().Be(0);
        summary.Promoters.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldClassifyBoundary_ScoreSeven_AsPassive()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns([MakeResponse("7")]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.Passives.Should().Be(1);
        summary.Promoters.Should().Be(0);
        summary.Detractors.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldClassifyBoundary_ScoreNine_AsPromoter()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns([MakeResponse("9")]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.Promoters.Should().Be(1);
        summary.Passives.Should().Be(0);
        summary.Detractors.Should().Be(0);
        summary.NpsScore.Should().BeApproximately(100d, 0.001);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldReturnZeroNps_WhenNoResponses()
    {
        _responseStore.GetBySurveyAsync(_tenant, _surveyId, Arg.Any<CancellationToken>())
            .Returns([]);

        var summary = await _analytics.GetSummaryAsync(_tenant, _surveyId, CancellationToken.None);

        summary.TotalResponses.Should().Be(0);
        summary.NpsScore.Should().BeNull();
    }

    // -------------------------------------------------------------------------

    private Survey MakeSurvey() =>
        new()
        {
            SurveyId = _surveyId,
            TenantId = _tenant,
            Name = "NPS Survey",
            Type = SurveyType.Nps,
            Questions = [],
        };

    private SurveyResponse MakeResponse(string score) =>
        new()
        {
            ResponseId = EntityId.New(),
            SurveyId = _surveyId,
            TenantId = _tenant,
            ConversationId = EntityId.New(),
            ContactId = EntityId.New(),
            Answers = [new SurveyAnswer(_qId, score)],
            SubmittedAt = DateTimeOffset.UtcNow,
        };
}
