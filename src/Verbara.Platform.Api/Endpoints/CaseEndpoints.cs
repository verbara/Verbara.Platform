using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class CaseEndpoints
{
    public static void MapCaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/cases").RequireAuthorization("Authenticated").RequireOperationalTenant();

        group.MapGet("/", ListCases);
        group.MapGet("/{id}", GetCase);
        group.MapPost("/", CreateCase);
        group.MapPut("/{id}", UpdateCase);
        group.MapPost("/{id}/conversations/{conversationId}", LinkConversation);
    }

    private static async Task<Ok<PagedResult<Case>>> ListCases(
        HttpContext context,
        [FromServices] ICaseStore store,
        [FromQuery] string? status,
        [FromQuery] string? priority,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var query = new PagedQuery { Page = page, PageSize = pageSize };
        var result = await store.ListAsync(tenantId, query, ct);

        // Apply in-memory filters (store returns paged, we filter after)
        var items = result.Items.AsEnumerable();

        if (status is not null && Enum.TryParse<CaseStatus>(status, ignoreCase: true, out var s))
            items = items.Where(c => c.Status == s);

        if (priority is not null && Enum.TryParse<CasePriority>(priority, ignoreCase: true, out var p))
            items = items.Where(c => c.Priority == p);

        var filtered = items.ToList();
        return TypedResults.Ok(new PagedResult<Case>(filtered, result.TotalCount, page, pageSize));
    }

    private static async Task<Results<Ok<Case>, NotFound>> GetCase(
        string id,
        HttpContext context,
        [FromServices] ICaseStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var caseEntity = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return caseEntity is null ? TypedResults.NotFound() : TypedResults.Ok(caseEntity);
    }

    private static async Task<IResult> CreateCase(
        HttpContext context,
        [FromBody] CreateCaseRequest body,
        [FromServices] ICaseStore store,
        [FromServices] IContactStore contactStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        // Validate contact exists
        var contact = await contactStore.GetByIdAsync(tenantId, EntityId.From(body.ContactId), ct);
        if (contact is null)
            return Results.BadRequest(new ErrorResponse($"Contact '{body.ContactId}' not found"));

        if (!Enum.TryParse<CasePriority>(body.Priority, ignoreCase: true, out var priority))
            return Results.BadRequest(new ErrorResponse($"Invalid priority '{body.Priority}'. Valid: Low, Normal, High, Urgent"));

        var caseNumber = $"CASE-{clock.UtcNow.Ticks % 100000000:D8}";

        var caseEntity = new Case
        {
            CaseId = EntityId.New(),
            TenantId = tenantId,
            CaseNumber = caseNumber,
            Subject = body.Subject,
            Priority = priority,
            Status = CaseStatus.Open,
            ContactId = EntityId.From(body.ContactId),
            AssignedAgentId = body.AssignedAgentId is not null ? EntityId.From(body.AssignedAgentId) : null,
            CreatedAt = clock.UtcNow,
            CreatedBy = context.User.FindFirst("sub")?.Value,
        };

        await store.SaveAsync(caseEntity, ct);
        return Results.Created($"/cases/{caseEntity.CaseId.Value}", caseEntity);
    }

    private static async Task<Results<Ok<Case>, NotFound, BadRequest<ErrorResponse>>> UpdateCase(
        string id,
        HttpContext context,
        [FromBody] UpdateCaseRequest body,
        [FromServices] ICaseStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return TypedResults.NotFound();

        if (body.Subject is not null)
            existing.Subject = body.Subject;

        if (body.Status is not null)
        {
            if (!Enum.TryParse<CaseStatus>(body.Status, ignoreCase: true, out var status))
                return TypedResults.BadRequest(new ErrorResponse($"Invalid status '{body.Status}'"));
            existing.Status = status;
        }

        if (body.Priority is not null)
        {
            if (!Enum.TryParse<CasePriority>(body.Priority, ignoreCase: true, out var priority))
                return TypedResults.BadRequest(new ErrorResponse($"Invalid priority '{body.Priority}'"));
            existing.Priority = priority;
        }

        if (body.AssignedAgentId is not null)
            existing.AssignedAgentId = EntityId.From(body.AssignedAgentId);

        existing.UpdatedAt = clock.UtcNow;
        existing.UpdatedBy = context.User.FindFirst("sub")?.Value;

        await store.SaveAsync(existing, ct);
        return TypedResults.Ok(existing);
    }

    private static async Task<Results<Ok<Case>, NotFound<ErrorResponse>>> LinkConversation(
        string id,
        string conversationId,
        HttpContext context,
        [FromServices] ICaseStore caseStore,
        [FromServices] IConversationStore conversationStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var caseEntity = await caseStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (caseEntity is null)
            return TypedResults.NotFound(new ErrorResponse($"Case '{id}' not found"));

        var conversation = await conversationStore.GetByIdAsync(tenantId, EntityId.From(conversationId), ct);
        if (conversation is null)
            return TypedResults.NotFound(new ErrorResponse($"Conversation '{conversationId}' not found"));

        caseEntity.AddConversation(EntityId.From(conversationId));
        await caseStore.SaveAsync(caseEntity, ct);

        return TypedResults.Ok(caseEntity);
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;
        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record CreateCaseRequest(
    string Subject,
    string Priority,
    string ContactId,
    string? AssignedAgentId);

internal sealed record UpdateCaseRequest(
    string? Subject,
    string? Status,
    string? Priority,
    string? AssignedAgentId);
