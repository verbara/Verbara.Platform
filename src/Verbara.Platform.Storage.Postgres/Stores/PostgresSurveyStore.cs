using System.Text.Json;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresSurveyStore : ISurveyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSurveyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Survey?> GetByIdAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT survey_id, tenant_id, name, type, questions, is_active " +
            "FROM surveys WHERE tenant_id = @TenantId AND survey_id = @SurveyId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("SurveyId", surveyId.Value)); },
            SurveyRow.Map, ct);
        return row?.ToSurvey();
    }

    public async Task<IReadOnlyList<Survey>> GetActiveAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT survey_id, tenant_id, name, type, questions, is_active " +
            "FROM surveys WHERE tenant_id = @TenantId AND is_active = true",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            SurveyRow.Map, ct);
        return rows.Select(r => r.ToSurvey()).ToList();
    }

    public async Task<IReadOnlyList<Survey>> GetAllAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT survey_id, tenant_id, name, type, questions, is_active " +
            "FROM surveys WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            SurveyRow.Map, ct);
        return rows.Select(r => r.ToSurvey()).ToList();
    }

    public async Task SaveAsync(Survey survey, CancellationToken ct)
    {
        var questionsJson = JsonSerializer.Serialize(survey.Questions, PostgresJson.Ctx.IReadOnlyListSurveyQuestion);

        await _dataSource.ExecuteAsync(
            "INSERT INTO surveys (survey_id, tenant_id, name, type, questions, is_active) " +
            "VALUES (@SurveyId, @TenantId, @Name, @Type, @Questions::jsonb, @IsActive) " +
            "ON CONFLICT (tenant_id, survey_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, type = EXCLUDED.type, questions = EXCLUDED.questions, " +
            "  is_active = EXCLUDED.is_active",
            p =>
            {
                p.Add(new NpgsqlParameter("SurveyId", survey.SurveyId.Value));
                p.Add(new NpgsqlParameter("TenantId", survey.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", survey.Name));
                p.Add(new NpgsqlParameter("Type", (int)survey.Type));
                p.Add(new NpgsqlParameter("Questions", questionsJson));
                p.Add(new NpgsqlParameter("IsActive", survey.IsActive));
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM surveys WHERE tenant_id = @TenantId AND survey_id = @SurveyId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("SurveyId", surveyId.Value)); },
            ct);
    }

    private sealed class SurveyRow
    {
        public string survey_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public int type { get; init; }
        public string questions { get; init; } = null!;
        public bool is_active { get; init; }

        public static SurveyRow Map(NpgsqlDataReader r) => new()
        {
            survey_id = r.GetString("survey_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            type = r.GetInt32("type"),
            questions = r.GetString("questions"),
            is_active = r.GetBoolean("is_active"),
        };

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
