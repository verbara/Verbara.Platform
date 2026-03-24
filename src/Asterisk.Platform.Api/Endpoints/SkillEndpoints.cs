using Asterisk.Sdk.Pro.Routing.Models;
using Asterisk.Sdk.Pro.Routing.Skills;

namespace Asterisk.Platform.Api.Endpoints;

internal static class SkillEndpoints
{
    public static void MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var skills = app.MapGroup("/api/admin/skills").RequireAuthorization("AdminOnly");

        skills.MapGet("/", ListSkills);
        skills.MapPost("/", CreateSkill);
        skills.MapPut("/{name}", UpsertSkill);
        skills.MapDelete("/{name}", DeleteSkill);

        var agentSkills = app.MapGroup("/api/admin/agents").RequireAuthorization("AdminOnly");

        agentSkills.MapGet("/{agentId}/skills", GetAgentSkills);
        agentSkills.MapPost("/{agentId}/skills", AssignSkill);
        agentSkills.MapDelete("/{agentId}/skills/{skillName}", RemoveAgentSkill);
    }

    // ─── Skill Definition Handlers ────────────────────────────────────────────

    private static async Task<IResult> ListSkills(
        SkillCatalogBase catalog,
        CancellationToken ct)
    {
        var skills = await catalog.GetSkillsAsync(ct);
        return Results.Ok(skills.Select(MapToDto).ToList());
    }

    private static async Task<IResult> CreateSkill(
        CreateSkillRequest body,
        SkillCatalogBase catalog,
        CancellationToken ct)
    {
        var skill = new Skill
        {
            Name = body.Name,
            Category = body.Category,
            Description = body.Description,
        };
        await catalog.AddSkillAsync(skill, ct);
        return Results.Created($"/api/admin/skills/{skill.Name}", MapToDto(skill));
    }

    private static async Task<IResult> UpsertSkill(
        string name,
        UpsertSkillRequest body,
        SkillCatalogBase catalog,
        CancellationToken ct)
    {
        var skill = new Skill
        {
            Name = name,
            Category = body.Category,
            Description = body.Description,
        };
        await catalog.AddSkillAsync(skill, ct);
        return Results.Ok(MapToDto(skill));
    }

    private static Task<IResult> DeleteSkill(
        string name,
        SkillCatalogBase catalog,
        CancellationToken ct)
    {
        // Skill definition removal is not supported in the current catalog version.
        return Task.FromResult(Results.Problem(
            title: "Not Supported",
            detail: "Skill definition deletion is not supported by the active catalog provider.",
            statusCode: 501));
    }

    // ─── Agent Skill Handlers ─────────────────────────────────────────────────

    private static async Task<IResult> GetAgentSkills(
        string agentId,
        SkillCatalogBase catalog,
        CancellationToken ct)
    {
        var agentSkills = await catalog.GetAgentSkillsAsync(agentId, ct);
        return Results.Ok(agentSkills.Select(MapAgentSkillToDto).ToList());
    }

    private static async Task<IResult> AssignSkill(
        string agentId,
        AssignSkillRequest body,
        SkillCatalogBase catalog,
        CancellationToken ct)
    {
        var agentSkill = new AgentSkill
        {
            AgentId = agentId,
            SkillName = body.SkillName,
            Proficiency = body.Proficiency ?? 5,
        };
        await catalog.AssignSkillAsync(agentSkill, ct);
        return Results.Created($"/api/admin/agents/{agentId}/skills/{agentSkill.SkillName}", MapAgentSkillToDto(agentSkill));
    }

    private static async Task<IResult> RemoveAgentSkill(
        string agentId,
        string skillName,
        SkillCatalogBase catalog,
        CancellationToken ct)
    {
        await catalog.RemoveSkillAsync(agentId, skillName, ct);
        return Results.NoContent();
    }

    // ─── Mapping Helpers ─────────────────────────────────────────────────────

    private static SkillDto MapToDto(Skill s) =>
        new(s.Name, s.Category, s.Description);

    private static AgentSkillDto MapAgentSkillToDto(AgentSkill a) =>
        new(a.AgentId, a.SkillName, a.Proficiency);
}

// ─── Request/Response DTOs ────────────────────────────────────────────────────

internal sealed record CreateSkillRequest(string Name, string? Category, string? Description);
internal sealed record UpsertSkillRequest(string? Category, string? Description);
internal sealed record AssignSkillRequest(string SkillName, int? Proficiency);
internal sealed record SkillDto(string Name, string? Category, string? Description);
internal sealed record AgentSkillDto(string AgentId, string SkillName, int Proficiency);
