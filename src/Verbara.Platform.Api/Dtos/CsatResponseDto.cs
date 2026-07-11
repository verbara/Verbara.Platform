namespace Verbara.Platform.Api.Dtos;

/// <summary>
/// Per-queue CSAT analytics summary returned by
/// <c>GET /api/v1/analytics/csat/queues/{queueId}</c> (csat-runner Phase B). Projects
/// the <c>Verbara.Platform.Surveys.SurveyScoreSummary</c> computed by
/// <c>ISurveyAnalytics.GetByQueueAndChannelAsync</c> over the channel-indexed rows.
/// </summary>
/// <param name="QueueName">The queue the aggregates cover.</param>
/// <param name="Channel">The channel filter the aggregates were computed for.</param>
/// <param name="TotalResponses">Number of CSAT responses in the requested range.</param>
/// <param name="AverageRating">Mean CSAT rating (1..5) across the responses; 0 when none.</param>
/// <param name="RangeStart">Inclusive start of the captured-at range.</param>
/// <param name="RangeEnd">Inclusive end of the captured-at range.</param>
internal sealed record CsatResponseDto(
    string QueueName,
    string Channel,
    int TotalResponses,
    double AverageRating,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd);
