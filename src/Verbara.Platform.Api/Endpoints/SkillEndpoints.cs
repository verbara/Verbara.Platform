using System.Text.Json;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class SkillEndpoints
{
    public static void MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var skills = app.MapGroup("/admin/skills")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.Routing);

        skills.MapGet("/", ListSkills);
        skills.MapPost("/", CreateSkill);
        skills.MapPut("/{name}", UpsertSkill);
        skills.MapDelete("/{name}", DeleteSkill);
        skills.MapGet("/{name}/agents", ListAgentsWithSkill);

        var agentSkills = app.MapGroup("/admin/agents")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.Routing);

        agentSkills.MapGet("/{agentId}/skills", GetAgentSkills);
        agentSkills.MapPost("/{agentId}/skills", AssignSkill);
        agentSkills.MapDelete("/{agentId}/skills/{skillName}", RemoveAgentSkill);
    }

    // ─── Skill Definition Handlers ────────────────────────────────────────────
    // Every handler reads and writes the caller's tenant only: the store takes the tenant first.

    private static async Task<Ok<List<SkillDto>>> ListSkills(
        HttpContext context,
        [FromServices] ISkillCatalogStore catalog,
        CancellationToken ct)
    {
        var skills = await catalog.ListSkillsAsync(GetTenantId(context), ct);
        return TypedResults.Ok(skills.Select(MapToDto).ToList());
    }

    private static async Task<IResult> CreateSkill(
        HttpContext context,
        [FromBody] CreateSkillRequest body,
        [FromServices] ISkillCatalogStore catalog,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var skill = await catalog.UpsertSkillAsync(
            tenantId, new SkillDefinition(body.Name, body.Category, body.Description), ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "skill.created", severity: "info",
            actorId: ActorId(context), actorType: "user",
            targetId: skill.Name, targetType: "skill",
            changes: new AuditChanges(Before: null, After: MapToDto(skill)),
            metadata: RequestMetadata(context),
            ct: ct);
        return Results.Created($"/admin/skills/{skill.Name}", MapToDto(skill));
    }

    private static async Task<Ok<SkillDto>> UpsertSkill(
        string name,
        HttpContext context,
        [FromBody] UpsertSkillRequest body,
        [FromServices] ISkillCatalogStore catalog,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var skill = await catalog.UpsertSkillAsync(
            tenantId, new SkillDefinition(name, body.Category, body.Description), ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "skill.updated", severity: "info",
            actorId: ActorId(context), actorType: "user",
            targetId: name, targetType: "skill",
            changes: new AuditChanges(Before: null, After: MapToDto(skill)),
            metadata: RequestMetadata(context),
            ct: ct);
        return TypedResults.Ok(MapToDto(skill));
    }

    /// <summary>
    /// Deletes the tenant's skill as the store decides it in one transaction: 404 when the tenant defines no skill
    /// of that name (with or without force, nothing removed, nothing recorded); 409 when, without force, some of the
    /// tenant's agents hold it; otherwise 204 with the definition and the tenant's assignments of it removed.
    /// </summary>
    private static async Task<IResult> DeleteSkill(
        string name,
        HttpContext context,
        [FromServices] ISkillCatalogStore catalog,
        [FromServices] IAuditService audit,
        bool force = false,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var removal = await catalog.RemoveSkillAsync(tenantId, name, force, ct);
        switch (removal.Outcome)
        {
            case SkillRemovalOutcome.InUse:
                return Results.Problem(
                    title: "Skill In Use",
                    detail: $"Skill '{name}' is assigned to {removal.HolderCount} agent(s). Use ?force=true to delete anyway.",
                    statusCode: 409);
            case SkillRemovalOutcome.Removed:
                await audit.RecordAsync(
                    tenantId, category: "config", action: "skill.deleted", severity: "info",
                    actorId: ActorId(context), actorType: "user",
                    targetId: name, targetType: "skill",
                    changes: new AuditChanges(Before: name, After: null),
                    metadata: RequestMetadata(context),
                    ct: ct);
                return Results.NoContent();
            default:
                return Results.NotFound();
        }
    }

    // ─── Agent Skill Handlers ─────────────────────────────────────────────────
    // Each route first finds the agent among the caller's tenant's agents and answers 404 when it is not there,
    // whichever tenant the agent may belong to. The audited assignment is the one the write itself replaced or
    // deleted, never one read before it.

    private static async Task<Results<Ok<List<AgentSkillDto>>, NotFound>> GetAgentSkills(
        string agentId,
        HttpContext context,
        [FromServices] IAgentStore agents,
        [FromServices] ISkillCatalogStore catalog,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (await FindAgentAsync(agents, tenantId, agentId, ct) is not { } agent)
            return TypedResults.NotFound();

        var agentSkills = await catalog.ListAgentSkillsAsync(tenantId, agent, ct);
        return TypedResults.Ok(agentSkills.Select(MapAgentSkillToDto).ToList());
    }

    private static async Task<Results<Created<AgentSkillDto>, NotFound>> AssignSkill(
        string agentId,
        HttpContext context,
        [FromBody] AssignSkillRequest body,
        [FromServices] IAgentStore agents,
        [FromServices] ISkillCatalogStore catalog,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (await FindAgentAsync(agents, tenantId, agentId, ct) is not { } agent)
            return TypedResults.NotFound();

        var assignment = await catalog.AssignAsync(
            tenantId, new AgentSkillProficiency(agent, body.SkillName, body.Proficiency ?? 5), ct);
        // The agent can be deleted between the check above and the write; the store reports it rather than storing.
        if (assignment is not { Outcome: SkillAssignOutcome.Assigned, Stored: { } stored })
            return TypedResults.NotFound();

        var after = MapAgentSkillToDto(stored);
        await audit.RecordAsync(
            tenantId, category: "config", action: "skill.assigned", severity: "info",
            actorId: ActorId(context), actorType: "user",
            targetId: agent.Value, targetType: "agent",
            changes: new AuditChanges(
                Before: assignment.Replaced is { } replaced ? AuditedAssignment(MapAgentSkillToDto(replaced)) : null,
                After: AuditedAssignment(after)),
            metadata: RequestMetadata(context),
            ct: ct);
        return TypedResults.Created($"/admin/agents/{agentId}/skills/{after.SkillName}", after);
    }

    private static async Task<Results<NoContent, NotFound>> RemoveAgentSkill(
        string agentId,
        string skillName,
        HttpContext context,
        [FromServices] IAgentStore agents,
        [FromServices] ISkillCatalogStore catalog,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (await FindAgentAsync(agents, tenantId, agentId, ct) is not { } agent)
            return TypedResults.NotFound();

        var deleted = await catalog.UnassignAsync(tenantId, agent, skillName, ct);
        if (deleted is not null)
        {
            await audit.RecordAsync(
                tenantId, category: "config", action: "skill.unassigned", severity: "info",
                actorId: ActorId(context), actorType: "user",
                targetId: agent.Value, targetType: "agent",
                changes: new AuditChanges(Before: AuditedAssignment(MapAgentSkillToDto(deleted)), After: null),
                metadata: RequestMetadata(context),
                ct: ct);
        }
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<AgentSkillDto>>> ListAgentsWithSkill(
        string name,
        HttpContext context,
        [FromServices] ISkillCatalogStore catalog,
        CancellationToken ct)
    {
        var agentSkills = await catalog.ListAgentsWithSkillAsync(GetTenantId(context), name, ct);
        return TypedResults.Ok(agentSkills.Select(MapAgentSkillToDto).ToList());
    }

    // ─── Mapping Helpers ─────────────────────────────────────────────────────

    private static SkillDto MapToDto(SkillDefinition s) =>
        new(s.Name, s.Category, s.Description);

    private static AgentSkillDto MapAgentSkillToDto(AgentSkillProficiency a) =>
        new(a.AgentId.Value, a.SkillName, a.Proficiency);

    /// <summary>
    /// An assignment as the audit records it: a JSON string written through the source-generated context, so the
    /// Native AOT image stores the same object the JIT host does (the audit store passes JSON strings through).
    /// </summary>
    internal static string AuditedAssignment(AgentSkillDto assignment) =>
        JsonSerializer.Serialize(assignment, ApiJsonContext.Default.AgentSkillDto);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The caller's tenant's agent of that id, or null when the tenant has none (another tenant's included).</summary>
    private static async Task<EntityId?> FindAgentAsync(
        IAgentStore agents, TenantId tenantId, string agentId, CancellationToken ct)
    {
        if (!EntityId.IsValid(agentId))
            return null;
        var id = EntityId.From(agentId);
        return await agents.GetByIdAsync(tenantId, id, ct) is null ? null : id;
    }

    private static string ActorId(HttpContext context) =>
        context.User.FindFirst("sub")?.Value ?? "system";

    private static Dictionary<string, string> RequestMetadata(HttpContext context) => new()
    {
        ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        ["endpoint"] = context.Request.Path.Value ?? "",
    };

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ────────────────────────────────────────────────────

internal sealed record CreateSkillRequest(string Name, string? Category, string? Description);
internal sealed record UpsertSkillRequest(string? Category, string? Description);
internal sealed record AssignSkillRequest(string SkillName, int? Proficiency);
internal sealed record SkillDto(string Name, string? Category, string? Description);
internal sealed record AgentSkillDto(string AgentId, string SkillName, int Proficiency);
