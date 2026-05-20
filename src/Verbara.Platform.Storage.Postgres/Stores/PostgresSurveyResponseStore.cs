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
            "agent_id, answers, submitted_at) " +
            "VALUES (@ResponseId, @SurveyId, @TenantId, @ConversationId, @ContactId, " +
            "@AgentId, @Answers::jsonb, @SubmittedAt) " +
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
            },
            ct);
    }

    public async Task<IReadOnlyList<SurveyResponse>> GetByConversationAsync(
        TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT response_id, survey_id, tenant_id, conversation_id, contact_id, agent_id, answers, submitted_at " +
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
            "SELECT response_id, survey_id, tenant_id, conversation_id, contact_id, agent_id, answers, submitted_at " +
            "FROM survey_responses WHERE tenant_id = @TenantId AND survey_id = @SurveyId " +
            "ORDER BY submitted_at",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("SurveyId", surveyId.Value)); },
            ResponseRow.Map, ct);
        return rows.Select(r => r.ToResponse()).ToList();
    }

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
            };
        }
    }
}
