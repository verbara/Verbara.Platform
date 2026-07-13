using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>
/// In-memory implementation of <see cref="ISurveyAnalytics"/>.
/// Delegates persistence to <see cref="ISurveyResponseStore"/> and
/// <see cref="ISurveyStore"/> for question metadata.
/// </summary>
public sealed class InMemorySurveyAnalytics : ISurveyAnalytics
{
    private readonly ISurveyResponseStore _responseStore;
    private readonly ISurveyStore _surveyStore;

    public InMemorySurveyAnalytics(ISurveyResponseStore responseStore, ISurveyStore surveyStore)
    {
        _responseStore = responseStore;
        _surveyStore = surveyStore;
    }

    /// <inheritdoc />
    public async Task<SurveyScoreSummary> GetSummaryAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        var responses = await _responseStore.GetBySurveyAsync(tenantId, surveyId, ct).ConfigureAwait(false);
        var survey = await _surveyStore.GetByIdAsync(tenantId, surveyId, ct).ConfigureAwait(false);
        return Summarize(survey, responses);
    }

    /// <inheritdoc />
    public async Task<SurveyScoreSummary> GetByAgentAsync(TenantId tenantId, EntityId surveyId, EntityId agentId, CancellationToken ct)
    {
        var all = await _responseStore.GetBySurveyAsync(tenantId, surveyId, ct).ConfigureAwait(false);
        var filtered = all.Where(r => r.AgentId.HasValue && r.AgentId.Value.Value == agentId.Value).ToList();
        var survey = await _surveyStore.GetByIdAsync(tenantId, surveyId, ct).ConfigureAwait(false);
        return Summarize(survey, filtered);
    }

    /// <inheritdoc />
    public async Task<SurveyScoreSummary> GetByQueueAndChannelAsync(
        TenantId tenantId, string queueName, string channel, DateRange range, CancellationToken ct)
    {
        var responses = await _responseStore
            .GetByQueueAndChannelAsync(tenantId, queueName, channel, range, ct)
            .ConfigureAwait(false);
        return SummarizeRatings(responses);
    }

    /// <inheritdoc />
    public async Task<CsatScopeAggregate> GetScopeAggregateAsync(
        TenantId tenantId, string? channel, DateRange range, CancellationToken ct)
    {
        var responses = await _responseStore
            .GetByChannelAndRangeAsync(tenantId, channel, range, ct)
            .ConfigureAwait(false);

        // Group the rated rows by queue; compute per-queue totals + means and the response-weighted
        // scope roll-up (mirrors the Postgres GROUP BY queue_name path).
        var queues = responses
            .Where(r => r.Rating.HasValue && !string.IsNullOrEmpty(r.QueueName))
            .GroupBy(r => r.QueueName!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var ratings = g.Select(r => (double)r.Rating!.Value).ToList();
                return new CsatQueueAggregate(g.Key, ratings.Count, ratings.Average());
            })
            .ToList();

        var scopeTotal = queues.Sum(q => q.TotalResponses);
        var scopeAvg = scopeTotal > 0
            ? queues.Sum(q => q.AverageRating * q.TotalResponses) / scopeTotal
            : 0d;

        return new CsatScopeAggregate(scopeTotal, scopeAvg, queues);
    }

    /// <inheritdoc />
    [Obsolete("Use GetByQueueAndChannelAsync; removed in v2.19.0 (csat-runner Phase A / Pro ADR-0012 cadence).")]
    public async Task<SurveyScoreSummary> GetByQueueAsync(TenantId tenantId, EntityId surveyId, string queueName, CancellationToken ct)
    {
        var all = await _responseStore.GetBySurveyAsync(tenantId, surveyId, ct).ConfigureAwait(false);
        // Queue membership is carried in response metadata via conversation context.
        // Filter by a well-known metadata key "queue" if present on the answer value.
        // Since SurveyResponse has no direct queue field, we match responses whose
        // answers include a special "__queue" marker or simply return all (simplification
        // per spec: "filter by metadata"). Callers populate this via custom answers.
        var filtered = all
            .Where(r => r.Answers.Any(a =>
                a.QuestionId.Value == "__queue" &&
                string.Equals(a.Value, queueName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var survey = await _surveyStore.GetByIdAsync(tenantId, surveyId, ct).ConfigureAwait(false);
        return Summarize(survey, filtered);
    }

    // -------------------------------------------------------------------------

    // CSAT-flavored summary: averages the Rating column directly (per ADR-0020 a
    // CSAT row exposes Rating rather than parsing Answers[csatRatingQuestionId]).
    // NPS bands do not apply to a 1..5 CSAT rating, so promoter/passive/detractor
    // and NpsScore are null.
    private static SurveyScoreSummary SummarizeRatings(IReadOnlyList<SurveyResponse> responses)
    {
        var ratings = responses
            .Where(r => r.Rating.HasValue)
            .Select(r => (double)r.Rating!.Value)
            .ToList();

        if (ratings.Count == 0)
            return new SurveyScoreSummary(0, 0d, null, null, null, null);

        return new SurveyScoreSummary(ratings.Count, ratings.Average(), null, null, null, null);
    }

    private static SurveyScoreSummary Summarize(Survey? survey, IReadOnlyList<SurveyResponse> responses)
    {
        if (responses.Count == 0)
            return new SurveyScoreSummary(0, 0d, null, null, null, null);

        var type = survey?.Type ?? SurveyType.Custom;

        // Collect numeric scores from Scale answers (first Scale answer per response).
        var scores = responses
            .Select(r => r.Answers.FirstOrDefault(a => IsNumeric(a.Value)))
            .Where(a => a is not null)
            .Select(a => double.Parse(a!.Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        var average = scores.Count > 0 ? scores.Average() : 0d;

        if (type == SurveyType.Nps)
            return BuildNps(responses.Count, average, scores);

        return new SurveyScoreSummary(responses.Count, average, null, null, null, null);
    }

    private static SurveyScoreSummary BuildNps(int total, double average, List<double> scores)
    {
        var promoters = scores.Count(s => s >= 9);
        var passives = scores.Count(s => s is >= 7 and <= 8);
        var detractors = scores.Count(s => s <= 6);
        var nps = scores.Count > 0
            ? (promoters - detractors) / (double)scores.Count * 100d
            : 0d;

        return new SurveyScoreSummary(total, average, promoters, passives, detractors, nps);
    }

    private static bool IsNumeric(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out _);
}
