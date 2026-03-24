using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Surveys;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresSurveyStore : ISurveyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSurveyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Survey?> GetByIdAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<SurveyRow>(
            "SELECT survey_id, tenant_id, name, type, questions, is_active " +
            "FROM surveys WHERE tenant_id = @TenantId AND survey_id = @SurveyId",
            new { TenantId = tenantId.Value, SurveyId = surveyId.Value });
        return row?.ToSurvey();
    }

    public async Task<IReadOnlyList<Survey>> GetActiveAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SurveyRow>(
            "SELECT survey_id, tenant_id, name, type, questions, is_active " +
            "FROM surveys WHERE tenant_id = @TenantId AND is_active = true",
            new { TenantId = tenantId.Value });
        return rows.Select(r => r.ToSurvey()).ToList();
    }

    public async Task<IReadOnlyList<Survey>> GetAllAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SurveyRow>(
            "SELECT survey_id, tenant_id, name, type, questions, is_active " +
            "FROM surveys WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });
        return rows.Select(r => r.ToSurvey()).ToList();
    }

    public async Task SaveAsync(Survey survey, CancellationToken ct)
    {
        var questionsJson = JsonSerializer.Serialize(survey.Questions, PostgresJson.Ctx.IReadOnlyListSurveyQuestion);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO surveys (survey_id, tenant_id, name, type, questions, is_active) " +
            "VALUES (@SurveyId, @TenantId, @Name, @Type, @Questions::jsonb, @IsActive) " +
            "ON CONFLICT (tenant_id, survey_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, type = EXCLUDED.type, questions = EXCLUDED.questions, " +
            "  is_active = EXCLUDED.is_active",
            new
            {
                SurveyId = survey.SurveyId.Value,
                TenantId = survey.TenantId.Value,
                survey.Name,
                Type = (int)survey.Type,
                Questions = questionsJson,
                survey.IsActive,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM surveys WHERE tenant_id = @TenantId AND survey_id = @SurveyId",
            new { TenantId = tenantId.Value, SurveyId = surveyId.Value });
    }

    private sealed record SurveyRow(
        string survey_id,
        string tenant_id,
        string name,
        int type,
        string questions,
        bool is_active)
    {
        public Survey ToSurvey()
        {
            var questionList = JsonSerializer.Deserialize(questions, PostgresJson.Ctx.IReadOnlyListSurveyQuestion)
                               ?? (IReadOnlyList<SurveyQuestion>)[];
            return new Survey
            {
                SurveyId = EntityId.From(survey_id),
                TenantId = new TenantId(tenant_id),
                Name = name,
                Type = (SurveyType)type,
                Questions = questionList,
                IsActive = is_active,
            };
        }
    }
}
