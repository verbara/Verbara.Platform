using Asterisk.Platform.Audit;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class BotEndpoints
{
    public static void MapBotEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/bots").RequireAuthorization("AdminOnly");

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
        // [FromServices] IBotConfigStore does not expose a ListAsync method.
        // Return the default active bot configuration for the tenant.
        var bot = await store.GetDefaultAsync(tenantId, ct);
        return Results.Ok(bot is null ? [] : new[] { bot });
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
            DefaultFlowId = EntityId.From(body.DefaultFlowId),
            FallbackQueueId = body.FallbackQueueId is not null ? EntityId.From(body.FallbackQueueId) : null,
            ConfidenceThreshold = body.ConfidenceThreshold ?? 0.7,
            MaxTurnsBeforeHandoff = body.MaxTurnsBeforeHandoff ?? 20,
            IsActive = body.IsActive ?? true,
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
        return Results.Created($"/admin/bots/{config.BotId}", config);
    }

    private static async Task<IResult> GetBot(
        string id,
        HttpContext context,
        [FromServices] IBotConfigStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var config = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return config is null ? Results.NotFound() : Results.Ok(config);
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
        if (body.FallbackQueueId is not null) existing.FallbackQueueId = EntityId.From(body.FallbackQueueId);
        if (body.ConfidenceThreshold.HasValue) existing.ConfidenceThreshold = body.ConfidenceThreshold.Value;
        if (body.MaxTurnsBeforeHandoff.HasValue) existing.MaxTurnsBeforeHandoff = body.MaxTurnsBeforeHandoff.Value;
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
        return Results.Ok(existing);
    }

    private static async Task<IResult> DeleteBot(
        string id,
        HttpContext context,
        [FromServices] IBotConfigStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        // [FromServices] IBotConfigStore does not expose a DeleteAsync method.
        // Soft-delete by deactivating the bot configuration.
        var tenantId = GetTenantId(context);
        var config = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (config is null)
            return Results.NotFound();

        config.IsActive = false;
        await store.SaveAsync(config, ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "bot.deleted", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "bot",
            changes: new AuditChanges(Before: new { config.BotId, config.Name, IsActive = true }, After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record CreateBotRequest(
    string Name,
    string DefaultFlowId,
    string? FallbackQueueId = null,
    double? ConfidenceThreshold = null,
    int? MaxTurnsBeforeHandoff = null,
    bool? IsActive = null);

internal sealed record UpdateBotRequest(
    string? Name = null,
    string? FallbackQueueId = null,
    double? ConfidenceThreshold = null,
    int? MaxTurnsBeforeHandoff = null,
    bool? IsActive = null);
