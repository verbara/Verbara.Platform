using Verbara.Platform.Audit;
using Verbara.Platform.Bot;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class BotEndpoints
{
    public static void MapBotEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/bots")
            .RequireAuthorization("AdminOnly")
            .RequirePlanFeature(PlanFeature.BotBasic);

        group.MapGet("/", ListBots);
        group.MapPost("/", CreateBot);
        group.MapGet("/{id}", GetBot);
        group.MapPut("/{id}", UpdateBot);
        group.MapDelete("/{id}", DeleteBot);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListBots(
        HttpContext context,
        [FromServices] IBotConfigStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListAsync(tenantId, ct);
        return Results.Ok(items.Select(ToDto).ToArray());
    }

    private static async Task<IResult> CreateBot(
        HttpContext context,
        [FromBody] CreateBotRequest body,
        [FromServices] IBotConfigStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var config = new BotConfiguration
        {
            BotId = EntityId.New(),
            TenantId = tenantId,
            Name = body.Name,
            DefaultFlowId = body.DefaultFlowId is not null ? EntityId.From(body.DefaultFlowId) : null,
            FallbackQueueId = body.FallbackQueueId is not null ? EntityId.From(body.FallbackQueueId) : null,
            ConfidenceThreshold = body.ConfidenceThreshold ?? 0.7,
            MaxTurnsBeforeHandoff = body.MaxTurns ?? 20,
            IsActive = body.IsActive ?? true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(config, ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "bot.created", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: config.BotId.Value, targetType: "bot",
            changes: new AuditChanges(Before: null, After: new { config.BotId, config.Name, config.IsActive }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/admin/bots/{config.BotId}", ToDto(config));
    }

    private static async Task<IResult> GetBot(
        string id,
        HttpContext context,
        [FromServices] IBotConfigStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var config = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return config is null ? Results.NotFound() : Results.Ok(ToDto(config));
    }

    private static async Task<IResult> UpdateBot(
        string id,
        HttpContext context,
        [FromBody] UpdateBotRequest body,
        [FromServices] IBotConfigStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

        var before = new { existing.BotId, existing.Name, existing.IsActive };

        if (body.Name is not null) existing.Name = body.Name;
        if (body.DefaultFlowId is not null)
            existing.DefaultFlowId = body.DefaultFlowId.Length == 0 ? null : EntityId.From(body.DefaultFlowId);
        if (body.FallbackQueueId is not null)
            existing.FallbackQueueId = body.FallbackQueueId.Length == 0 ? null : EntityId.From(body.FallbackQueueId);
        if (body.ConfidenceThreshold.HasValue) existing.ConfidenceThreshold = body.ConfidenceThreshold.Value;
        if (body.MaxTurns.HasValue) existing.MaxTurnsBeforeHandoff = body.MaxTurns.Value;
        if (body.IsActive.HasValue) existing.IsActive = body.IsActive.Value;

        await store.SaveAsync(existing, ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "bot.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "bot",
            changes: new AuditChanges(Before: before, After: new { existing.BotId, existing.Name, existing.IsActive }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(ToDto(existing));
    }

    private static async Task<IResult> DeleteBot(
        string id,
        HttpContext context,
        [FromServices] IBotConfigStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

        var removed = await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        if (!removed)
            return Results.NotFound();

        await audit.RecordAsync(
            tenantId, category: "config", action: "bot.deleted", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "bot",
            changes: new AuditChanges(Before: new { existing.BotId, existing.Name, existing.IsActive }, After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private static BotDto ToDto(BotConfiguration c) => new(
        Id: c.BotId.Value,
        Name: c.Name,
        DefaultFlowId: c.DefaultFlowId?.Value,
        FallbackQueueId: c.FallbackQueueId?.Value,
        ConfidenceThreshold: c.ConfidenceThreshold,
        MaxTurns: c.MaxTurnsBeforeHandoff,
        IsActive: c.IsActive,
        CreatedAt: c.CreatedAt);

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record BotDto(
    string Id,
    string Name,
    string? DefaultFlowId,
    string? FallbackQueueId,
    double ConfidenceThreshold,
    int MaxTurns,
    bool IsActive,
    DateTimeOffset CreatedAt);

internal sealed record CreateBotRequest(
    string Name,
    string? DefaultFlowId = null,
    string? FallbackQueueId = null,
    double? ConfidenceThreshold = null,
    int? MaxTurns = null,
    bool? IsActive = null);

internal sealed record UpdateBotRequest(
    string? Name = null,
    string? DefaultFlowId = null,
    string? FallbackQueueId = null,
    double? ConfidenceThreshold = null,
    int? MaxTurns = null,
    bool? IsActive = null);
