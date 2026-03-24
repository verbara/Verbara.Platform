using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Surveys;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresSurveyResponseStore : ISurveyResponseStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSurveyResponseStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(SurveyResponse response, CancellationToken ct)
    {
        var answersJson = JsonSerializer.Serialize(response.Answers, PostgresJson.Ctx.IReadOnlyListSurveyAnswer);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO survey_responses (response_id, survey_id, tenant_id, conversation_id, contact_id, " +
            "agent_id, answers, submitted_at) " +
            "VALUES (@ResponseId, @SurveyId, @TenantId, @ConversationId, @ContactId, " +
            "@AgentId, @Answers::jsonb, @SubmittedAt) " +
            "ON CONFLICT (tenant_id, response_id) DO NOTHING",
            new
            {
                ResponseId = response.ResponseId.Value,
                SurveyId = response.SurveyId.Value,
                TenantId = response.TenantId.Value,
                ConversationId = response.ConversationId.Value,
                ContactId = response.ContactId.Value,
                AgentId = response.AgentId?.Value,
                Answers = answersJson,
                response.SubmittedAt,
            });
    }

    public async Task<IReadOnlyList<SurveyResponse>> GetByConversationAsync(
        TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ResponseRow>(
            "SELECT response_id, survey_id, tenant_id, conversation_id, contact_id, agent_id, answers, submitted_at " +
            "FROM survey_responses WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "ORDER BY submitted_at",
            new { TenantId = tenantId.Value, ConversationId = conversationId.Value });
        return rows.Select(r => r.ToResponse()).ToList();
    }

    public async Task<IReadOnlyList<SurveyResponse>> GetBySurveyAsync(
        TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ResponseRow>(
            "SELECT response_id, survey_id, tenant_id, conversation_id, contact_id, agent_id, answers, submitted_at " +
            "FROM survey_responses WHERE tenant_id = @TenantId AND survey_id = @SurveyId " +
            "ORDER BY submitted_at",
            new { TenantId = tenantId.Value, SurveyId = surveyId.Value });
        return rows.Select(r => r.ToResponse()).ToList();
    }

    private sealed record ResponseRow(
        string response_id,
        string survey_id,
        string tenant_id,
        string conversation_id,
        string contact_id,
        string? agent_id,
        string answers,
        DateTimeOffset submitted_at)
    {
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
                SubmittedAt = submitted_at,
            };
        }
    }
}
