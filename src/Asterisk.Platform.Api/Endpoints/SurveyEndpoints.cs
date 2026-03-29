using Asterisk.Platform.Core;
using Asterisk.Platform.Surveys;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class SurveyEndpoints
{
    public static void MapSurveyEndpoints(this IEndpointRouteBuilder app)
    {
        var adminGroup = app.MapGroup("/api/admin/surveys").RequireAuthorization("AdminOnly");

        adminGroup.MapGet("/", ListSurveys);
        adminGroup.MapPost("/", CreateSurvey);
        adminGroup.MapGet("/{id}", GetSurvey);
        adminGroup.MapPut("/{id}", UpdateSurvey);
        adminGroup.MapDelete("/{id}", DeleteSurvey);
        adminGroup.MapMethods("/{id}/activate", ["PATCH"], ActivateSurvey);

        var analyticsGroup = app.MapGroup("/api/analytics/surveys").RequireAuthorization("SupervisorPlus");

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
        return Results.Ok(surveys);
    }

    private static async Task<IResult> CreateSurvey(
        HttpContext context,
        [FromBody] CreateSurveyRequest body,
        [FromServices] ISurveyStore store,
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
        return Results.Created($"/api/admin/surveys/{survey.SurveyId}", survey);
    }

    private static async Task<IResult> GetSurvey(
        string id,
        HttpContext context,
        [FromServices] ISurveyStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var survey = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return survey is null ? Results.NotFound() : Results.Ok(survey);
    }

    private static async Task<IResult> UpdateSurvey(
        string id,
        HttpContext context,
        [FromBody] UpdateSurveyRequest body,
        [FromServices] ISurveyStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

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
        return Results.Ok(updated);
    }

    private static async Task<IResult> DeleteSurvey(
        string id,
        HttpContext context,
        [FromServices] ISurveyStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
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
        return Results.Ok(existing);
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

internal sealed record SurveyQuestionDto(
    string Text,
    SurveyQuestionType Type,
    IReadOnlyList<string>? Options = null);
