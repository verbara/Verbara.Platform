using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresSurveyResponseStore : ISurveyResponseStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSurveyResponseStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(SurveyResponse response, CancellationToken ct)
    {
        var answersJson = JsonSerializer.Serialize(response.Answers, PostgresJson.Ctx.IReadOnlyListSurveyAnswer);

        await _dataSource.ExecuteAsync(
            "INSERT INTO survey_responses (response_id, survey_id, tenant_id, conversation_id, contact_id, " +
            "agent_id, answers, submitted_at, channel, queue_name, rating, comment, captured_at, call_id) " +
            "VALUES (@ResponseId, @SurveyId, @TenantId, @ConversationId, @ContactId, " +
            "@AgentId, @Answers::jsonb, @SubmittedAt, @Channel, @QueueName, @Rating, @Comment, @CapturedAt, @CallId) " +
            "ON CONFLICT (tenant_id, response_id) DO NOTHING",
            p =>
            {
                p.Add(new NpgsqlParameter("ResponseId", response.ResponseId.Value));
                p.Add(new NpgsqlParameter("SurveyId", response.SurveyId.Value));
                p.Add(new NpgsqlParameter("TenantId", response.TenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", response.ConversationId.Value));
                p.Add(new NpgsqlParameter("ContactId", response.ContactId.Value));
                p.Add(new NpgsqlParameter("AgentId", NpgsqlDbType.Text) { Value = (object?)response.AgentId?.Value ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Answers", answersJson));
                p.Add(new NpgsqlParameter("SubmittedAt", response.SubmittedAt.UtcDateTime));
                // CSAT brownfield columns (csat-runner Phase A). Every nullable
                // param that can be DBNull.Value sets an explicit NpgsqlDbType,
                // else Postgres throws 42P08 (ADR-0022 idiom).
                p.Add(new NpgsqlParameter("Channel", NpgsqlDbType.Text) { Value = (object?)response.Channel ?? DBNull.Value });
                p.Add(new NpgsqlParameter("QueueName", NpgsqlDbType.Text) { Value = (object?)response.QueueName ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Rating", NpgsqlDbType.Integer) { Value = (object?)response.Rating ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Comment", NpgsqlDbType.Text) { Value = (object?)response.Comment ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CapturedAt", NpgsqlDbType.TimestampTz) { Value = (object?)response.CapturedAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CallId", NpgsqlDbType.Text) { Value = (object?)response.CallId?.Value ?? DBNull.Value });
            },
            ct);
    }

    public async Task<IReadOnlyList<SurveyResponse>> GetByConversationAsync(
        TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            SelectColumns +
            "FROM survey_responses WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "ORDER BY submitted_at",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ConversationId", conversationId.Value)); },
            ResponseRow.Map, ct);
        return rows.Select(r => r.ToResponse()).ToList();
    }

    public async Task<IReadOnlyList<SurveyResponse>> GetBySurveyAsync(
        TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            SelectColumns +
            "FROM survey_responses WHERE tenant_id = @TenantId AND survey_id = @SurveyId " +
            "ORDER BY submitted_at",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("SurveyId", surveyId.Value)); },
            ResponseRow.Map, ct);
        return rows.Select(r => r.ToResponse()).ToList();
    }

    public async Task<IReadOnlyList<SurveyResponse>> GetByQueueAndChannelAsync(
        TenantId tenantId, string queueName, string channel, DateRange range, CancellationToken ct)
    {
        // Served by idx_survey_resp_queue_captured
        // (tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL.
        var rows = await _dataSource.QueryListAsync(
            SelectColumns +
            "FROM survey_responses " +
            "WHERE tenant_id = @TenantId AND queue_name = @QueueName AND channel = @Channel " +
            "AND channel IS NOT NULL " +
            "AND captured_at >= @RangeStart AND captured_at <= @RangeEnd " +
            "ORDER BY captured_at DESC",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("QueueName", queueName));
                p.Add(new NpgsqlParameter("Channel", channel));
                p.Add(new NpgsqlParameter("RangeStart", NpgsqlDbType.TimestampTz) { Value = range.Start });
                p.Add(new NpgsqlParameter("RangeEnd", NpgsqlDbType.TimestampTz) { Value = range.End });
            },
            ResponseRow.Map, ct);
        return rows.Select(r => r.ToResponse()).ToList();
    }

    public async Task<IReadOnlyList<SurveyResponse>> GetByChannelAndRangeAsync(
        TenantId tenantId, string? channel, DateRange range, CancellationToken ct)
    {
        // Scope-wide (all queues) read over the same partial index (csat-completion). The channel filter
        // is optional — null/empty means all channels; the WHERE channel IS NOT NULL predicate keeps the
        // scan on the partial index either way.
        var hasChannel = !string.IsNullOrWhiteSpace(channel);
        var rows = await _dataSource.QueryListAsync(
            SelectColumns +
            "FROM survey_responses " +
            "WHERE tenant_id = @TenantId AND channel IS NOT NULL " +
            (hasChannel ? "AND channel = @Channel " : "") +
            "AND captured_at >= @RangeStart AND captured_at <= @RangeEnd " +
            "ORDER BY captured_at DESC",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                if (hasChannel)
                    p.Add(new NpgsqlParameter("Channel", channel!));
                p.Add(new NpgsqlParameter("RangeStart", NpgsqlDbType.TimestampTz) { Value = range.Start });
                p.Add(new NpgsqlParameter("RangeEnd", NpgsqlDbType.TimestampTz) { Value = range.End });
            },
            ResponseRow.Map, ct);
        return rows.Select(r => r.ToResponse()).ToList();
    }

    private const string SelectColumns =
        "SELECT response_id, survey_id, tenant_id, conversation_id, contact_id, agent_id, answers, submitted_at, " +
        "channel, queue_name, rating, comment, captured_at, call_id ";

    private sealed class ResponseRow
    {
        public string response_id { get; init; } = null!;
        public string survey_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string conversation_id { get; init; } = null!;
        public string contact_id { get; init; } = null!;
        public string? agent_id { get; init; }
        public string answers { get; init; } = null!;
        public DateTime submitted_at { get; init; }

        // CSAT brownfield columns (csat-runner Phase A) — all nullable.
        public string? channel { get; init; }
        public string? queue_name { get; init; }
        public int? rating { get; init; }
        public string? comment { get; init; }
        public DateTimeOffset? captured_at { get; init; }
        public string? call_id { get; init; }

        public static ResponseRow Map(NpgsqlDataReader r) => new()
        {
            response_id = r.GetString("response_id"),
            survey_id = r.GetString("survey_id"),
            tenant_id = r.GetString("tenant_id"),
            conversation_id = r.GetString("conversation_id"),
            contact_id = r.GetString("contact_id"),
            agent_id = r.GetStringOrNull("agent_id"),
            answers = r.GetString("answers"),
            submitted_at = r.GetDateTime("submitted_at"),
            channel = r.GetStringOrNull("channel"),
            queue_name = r.GetStringOrNull("queue_name"),
            rating = r.GetInt32OrNull("rating"),
            comment = r.GetStringOrNull("comment"),
            captured_at = r.GetDateTimeOffsetOrNull("captured_at"),
            call_id = r.GetStringOrNull("call_id"),
        };

        public SurveyResponse ToResponse()
        {
            var answerList = JsonSerializer.Deserialize(answers, PostgresJson.Ctx.IReadOnlyListSurveyAnswer)
                             ?? (IReadOnlyList<SurveyAnswer>)[];
            return new SurveyResponse
            {
                ResponseId = EntityId.From(response_id),
                SurveyId = EntityId.From(survey_id),
                TenantId = new TenantId(tenant_id),
                ConversationId = EntityId.From(conversation_id),
                ContactId = EntityId.From(contact_id),
                AgentId = agent_id != null ? EntityId.From(agent_id) : null,
                Answers = answerList,
                SubmittedAt = new DateTimeOffset(submitted_at, TimeSpan.Zero),
                Channel = channel,
                QueueName = queue_name,
                Rating = rating,
                Comment = comment,
                CapturedAt = captured_at,
                CallId = call_id != null ? EntityId.From(call_id) : null,
            };
        }
    }
}
