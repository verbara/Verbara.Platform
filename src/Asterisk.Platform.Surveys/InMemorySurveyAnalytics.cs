using Asterisk.Platform.Core;

namespace Asterisk.Platform.Surveys;

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
