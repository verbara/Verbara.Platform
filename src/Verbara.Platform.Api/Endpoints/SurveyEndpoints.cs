using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class SurveyEndpoints
{
    public static void MapSurveyEndpoints(this IEndpointRouteBuilder app)
    {
        var adminGroup = app.MapGroup("/admin/surveys").RequireAuthorization("AdminOnly").RequireOperationalTenant();

        adminGroup.MapGet("/", ListSurveys);
        adminGroup.MapPost("/", CreateSurvey);
        adminGroup.MapGet("/{id}", GetSurvey);
        adminGroup.MapPut("/{id}", UpdateSurvey);
        adminGroup.MapDelete("/{id}", DeleteSurvey);
        adminGroup.MapMethods("/{id}/activate", ["PATCH"], ActivateSurvey);

        var analyticsGroup = app.MapGroup("/analytics/surveys").RequireAuthorization("SupervisorPlus").RequireOperationalTenant();

        analyticsGroup.MapGet("/{id}/summary", GetSurveySummary);
        analyticsGroup.MapGet("/{id}/responses", GetSurveyResponses);
    }

    // ─── Admin CRUD ───────────────────────────────────────────────────────────

    private static async Task<IResult> ListSurveys(
        HttpContext context,
        [FromServices] ISurveyStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var surveys = await store.GetAllAsync(tenantId, ct);
        return Results.Ok(surveys.Select(ToSurveyDto).ToArray());
    }

    private static SurveyDto ToSurveyDto(Survey s) =>
        new(
            Id: s.SurveyId.Value,
            Name: s.Name,
            Type: s.Type.ToString(),
            Questions: s.Questions.Select(q => new SurveyQuestionDto(q.Text, q.Type, q.Options)).ToArray(),
            IsActive: s.IsActive);

    private static async Task<IResult> CreateSurvey(
        HttpContext context,
        [FromBody] CreateSurveyRequest body,
        [FromServices] ISurveyStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var questions = MapQuestions(body.Questions);
        var survey = new Survey
        {
            SurveyId = EntityId.New(),
            TenantId = tenantId,
            Name = body.Name,
            Type = body.Type,
            Questions = questions,
            IsActive = body.IsActive ?? true,
        };
        await store.SaveAsync(survey, ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "survey.created", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: survey.SurveyId.Value, targetType: "survey",
            changes: new AuditChanges(Before: null, After: new { survey.SurveyId, survey.Name, survey.Type }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/admin/surveys/{survey.SurveyId}", ToSurveyDto(survey));
    }

    private static async Task<IResult> GetSurvey(
        string id,
        HttpContext context,
        [FromServices] ISurveyStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var survey = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return survey is null ? Results.NotFound() : Results.Ok(ToSurveyDto(survey));
    }

    private static async Task<IResult> UpdateSurvey(
        string id,
        HttpContext context,
        [FromBody] UpdateSurveyRequest body,
        [FromServices] ISurveyStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

        var before = new { existing.SurveyId, existing.Name, existing.Type, existing.IsActive };

        var updated = new Survey
        {
            SurveyId = existing.SurveyId,
            TenantId = existing.TenantId,
            Name = body.Name ?? existing.Name,
            Type = body.Type ?? existing.Type,
            Questions = body.Questions is not null ? MapQuestions(body.Questions) : existing.Questions,
            IsActive = body.IsActive ?? existing.IsActive,
        };
        await store.SaveAsync(updated, ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "survey.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "survey",
            changes: new AuditChanges(Before: before, After: new { updated.SurveyId, updated.Name, updated.Type, updated.IsActive }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(ToSurveyDto(updated));
    }

    private static async Task<IResult> DeleteSurvey(
        string id,
        HttpContext context,
        [FromServices] ISurveyStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var before = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "survey.deleted", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "survey",
            changes: new AuditChanges(Before: before is null ? null : new { before.SurveyId, before.Name }, After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ActivateSurvey(
        string id,
        HttpContext context,
        [FromBody] ActivateSurveyRequest body,
        [FromServices] ISurveyStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

        existing.IsActive = body.IsActive;
        await store.SaveAsync(existing, ct);
        return Results.Ok(ToSurveyDto(existing));
    }

    // ─── Analytics ────────────────────────────────────────────────────────────

    private static async Task<IResult> GetSurveySummary(
        string id,
        HttpContext context,
        ISurveyAnalytics analytics,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var summary = await analytics.GetSummaryAsync(tenantId, EntityId.From(id), ct);
        return Results.Ok(summary);
    }

    private static async Task<IResult> GetSurveyResponses(
        string id,
        HttpContext context,
        [FromServices] ISurveyResponseStore responseStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var responses = await responseStore.GetBySurveyAsync(tenantId, EntityId.From(id), ct);
        return Results.Ok(responses);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static List<SurveyQuestion> MapQuestions(IReadOnlyList<SurveyQuestionDto> dtos) =>
        dtos.Select(q => new SurveyQuestion
        {
            QuestionId = EntityId.New(),
            Text = q.Text,
            Type = q.Type,
            Options = q.Options,
        }).ToList();

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record CreateSurveyRequest(
    string Name,
    SurveyType Type,
    IReadOnlyList<SurveyQuestionDto> Questions,
    bool? IsActive = null);

internal sealed record UpdateSurveyRequest(
    string? Name = null,
    SurveyType? Type = null,
    IReadOnlyList<SurveyQuestionDto>? Questions = null,
    bool? IsActive = null);

internal sealed record ActivateSurveyRequest(bool IsActive);

internal sealed record SurveyDto(
    string Id,
    string Name,
    string Type,
    IReadOnlyList<SurveyQuestionDto> Questions,
    bool IsActive);

internal sealed record SurveyQuestionDto(
    string Text,
    SurveyQuestionType Type,
    IReadOnlyList<string>? Options = null);
