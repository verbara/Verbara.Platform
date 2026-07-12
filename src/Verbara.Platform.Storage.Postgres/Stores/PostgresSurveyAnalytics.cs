using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres-backed <see cref="ISurveyAnalytics"/>. The CSAT per-queue path
/// (<see cref="GetByQueueAndChannelAsync"/>) runs a direct <c>COUNT</c>/<c>AVG</c>
/// aggregate served by the partial index
/// <c>idx_survey_resp_queue_captured (tenant_id, queue_name, captured_at DESC)
/// WHERE channel IS NOT NULL</c> (csat-runner Phase A) — computing the summary in
/// the database rather than pulling every row, for the &lt;2s supervisor-dashboard
/// SLA. The survey/agent-scoped methods delegate to the shared
/// <see cref="InMemorySurveyAnalytics"/> summarizer (same math, over the store's
/// rows), so this class only owns the aggregate the indexes exist for.
/// </summary>
internal sealed class PostgresSurveyAnalytics : ISurveyAnalytics
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly InMemorySurveyAnalytics _inner;

    public PostgresSurveyAnalytics(
        NpgsqlDataSource dataSource,
        ISurveyResponseStore responseStore,
        ISurveyStore surveyStore)
    {
        _dataSource = dataSource;
        _inner = new InMemorySurveyAnalytics(responseStore, surveyStore);
    }

    public Task<SurveyScoreSummary> GetSummaryAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
        => _inner.GetSummaryAsync(tenantId, surveyId, ct);

    public Task<SurveyScoreSummary> GetByAgentAsync(TenantId tenantId, EntityId surveyId, EntityId agentId, CancellationToken ct)
        => _inner.GetByAgentAsync(tenantId, surveyId, agentId, ct);

    public async Task<SurveyScoreSummary> GetByQueueAndChannelAsync(
        TenantId tenantId, string queueName, string channel, DateRange range, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT COUNT(*) AS total, AVG(rating)::double precision AS avg_rating " +
            "FROM survey_responses " +
            "WHERE tenant_id = @TenantId AND queue_name = @QueueName AND channel = @Channel " +
            "AND channel IS NOT NULL AND rating IS NOT NULL " +
            "AND captured_at >= @RangeStart AND captured_at <= @RangeEnd",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("QueueName", queueName));
                p.Add(new NpgsqlParameter("Channel", channel));
                p.Add(new NpgsqlParameter("RangeStart", NpgsqlDbType.TimestampTz) { Value = range.Start });
                p.Add(new NpgsqlParameter("RangeEnd", NpgsqlDbType.TimestampTz) { Value = range.End });
            },
            AggregateRow.Map, ct).ConfigureAwait(false);

        if (row is null || row.Total == 0)
            return new SurveyScoreSummary(0, 0d, null, null, null, null);

        // NPS bands do not apply to a 1..5 CSAT rating (ADR-0020).
        return new SurveyScoreSummary((int)row.Total, row.AvgRating ?? 0d, null, null, null, null);
    }

    [Obsolete("Use GetByQueueAndChannelAsync; removed in v2.19.0 (csat-runner Phase A / Pro ADR-0012 cadence).")]
    public Task<SurveyScoreSummary> GetByQueueAsync(TenantId tenantId, EntityId surveyId, string queueName, CancellationToken ct)
#pragma warning disable CS0618 // delegating the obsolete member to preserve the legacy behavior until v2.19.0 removal
        => _inner.GetByQueueAsync(tenantId, surveyId, queueName, ct);
#pragma warning restore CS0618

    private sealed class AggregateRow
    {
        public long Total { get; init; }
        public double? AvgRating { get; init; }

        public static AggregateRow Map(NpgsqlDataReader r) => new()
        {
            Total = r.GetInt64("total"),
            AvgRating = r.IsDBNull(r.GetOrdinal("avg_rating")) ? null : r.GetDouble("avg_rating"),
        };
    }
}
