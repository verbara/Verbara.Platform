using Verbara.Platform.Core;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Surveys.Tests;

/// <summary>
/// Channel-filter cases for <see cref="InMemorySurveyAnalytics.GetByQueueAndChannelAsync"/>
/// (csat-runner Phase A). Exercises the CSAT-flavored summary path, which averages
/// the <c>Rating</c> column directly (ADR-0020) and filters by queue + channel +
/// captured-at range. The in-memory store's <c>GetByQueueAndChannelAsync</c> does the
/// filtering; here we assert the analytics delegate + summarize behavior via a
/// substitute so the two concerns stay separable.
/// </summary>
public class QueueChannelAnalyticsTests
{
    private readonly ISurveyResponseStore _responseStore = Substitute.For<ISurveyResponseStore>();
    private readonly ISurveyStore _surveyStore = Substitute.For<ISurveyStore>();
    private readonly InMemorySurveyAnalytics _analytics;

    private readonly TenantId _tenant = new("t1");
    private readonly DateRange _range = new(
        new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero));

    public QueueChannelAnalyticsTests() =>
        _analytics = new InMemorySurveyAnalytics(_responseStore, _surveyStore);

    [Fact]
    public async Task GetByQueueAndChannelAsync_ShouldAverageRatings_WhenResponsesPresent()
    {
        _responseStore
            .GetByQueueAndChannelAsync(_tenant, "support-tier1", "webchat", _range, Arg.Any<CancellationToken>())
            .Returns(
            [
                CsatResponse("webchat", "support-tier1", 5),
                CsatResponse("webchat", "support-tier1", 4),
                CsatResponse("webchat", "support-tier1", 3),
            ]);

        var summary = await _analytics.GetByQueueAndChannelAsync(
            _tenant, "support-tier1", "webchat", _range, CancellationToken.None);

        summary.TotalResponses.Should().Be(3);
        summary.AverageScore.Should().BeApproximately(4.0, 0.001);
        summary.NpsScore.Should().BeNull();
        summary.Promoters.Should().BeNull();
    }

    [Fact]
    public async Task GetByQueueAndChannelAsync_ShouldReturnZero_WhenNoResponses()
    {
        _responseStore
            .GetByQueueAndChannelAsync(_tenant, "support-tier1", "sms", _range, Arg.Any<CancellationToken>())
            .Returns([]);

        var summary = await _analytics.GetByQueueAndChannelAsync(
            _tenant, "support-tier1", "sms", _range, CancellationToken.None);

        summary.TotalResponses.Should().Be(0);
        summary.AverageScore.Should().Be(0d);
    }

    [Fact]
    public async Task GetByQueueAndChannelAsync_ShouldPassChannelFilterToStore_WhenQueried()
    {
        _responseStore
            .GetByQueueAndChannelAsync(_tenant, "sales", "email", _range, Arg.Any<CancellationToken>())
            .Returns([CsatResponse("email", "sales", 2)]);

        var summary = await _analytics.GetByQueueAndChannelAsync(
            _tenant, "sales", "email", _range, CancellationToken.None);

        summary.TotalResponses.Should().Be(1);
        summary.AverageScore.Should().BeApproximately(2.0, 0.001);
        await _responseStore.Received(1).GetByQueueAndChannelAsync(
            _tenant, "sales", "email", _range, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByQueueAndChannelAsync_ShouldIgnoreRowsWithoutRating_WhenSummarizing()
    {
        _responseStore
            .GetByQueueAndChannelAsync(_tenant, "support-tier1", "webchat", _range, Arg.Any<CancellationToken>())
            .Returns(
            [
                CsatResponse("webchat", "support-tier1", 5),
                CsatResponseNoRating("webchat", "support-tier1"),
            ]);

        var summary = await _analytics.GetByQueueAndChannelAsync(
            _tenant, "support-tier1", "webchat", _range, CancellationToken.None);

        summary.TotalResponses.Should().Be(1);
        summary.AverageScore.Should().BeApproximately(5.0, 0.001);
    }

    // -------------------------------------------------------------------------

    private SurveyResponse CsatResponse(string channel, string queueName, int rating) => new()
    {
        ResponseId = EntityId.New(),
        SurveyId = EntityId.From("srv-csat-v1"),
        TenantId = _tenant,
        ConversationId = EntityId.New(),
        ContactId = EntityId.New(),
        Answers = [],
        SubmittedAt = new DateTimeOffset(2026, 7, 7, 9, 15, 0, TimeSpan.Zero),
        Channel = channel,
        QueueName = queueName,
        Rating = rating,
        CapturedAt = new DateTimeOffset(2026, 7, 7, 9, 15, 0, TimeSpan.Zero),
    };

    private SurveyResponse CsatResponseNoRating(string channel, string queueName) => new()
    {
        ResponseId = EntityId.New(),
        SurveyId = EntityId.From("srv-csat-v1"),
        TenantId = _tenant,
        ConversationId = EntityId.New(),
        ContactId = EntityId.New(),
        Answers = [],
        SubmittedAt = new DateTimeOffset(2026, 7, 7, 9, 15, 0, TimeSpan.Zero),
        Channel = channel,
        QueueName = queueName,
        Rating = null,
        CapturedAt = new DateTimeOffset(2026, 7, 7, 9, 15, 0, TimeSpan.Zero),
    };
}
