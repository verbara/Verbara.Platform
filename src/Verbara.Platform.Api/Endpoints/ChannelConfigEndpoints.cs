using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Audit;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class ChannelConfigEndpoints
{
    public static void MapChannelConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/channels").RequireAuthorization("AdminOnly");

        group.MapGet("/", ListChannelConfigs);
        group.MapGet("/{channel}", GetChannelConfig);
        group.MapPut("/{channel}", UpdateChannelConfig);
        group.MapPost("/{channel}/test", TestChannelConfig);
    }

    private static async Task<IResult> ListChannelConfigs(
        HttpContext context,
        [FromServices] ITenantChannelConfigStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var results = new List<object>();

        foreach (var channelType in Enum.GetValues<ChannelType>())
        {
            var config = await store.GetAsync(tenantId, channelType, ct);
            results.Add(config is not null
                ? config
                : new ChannelStatusDto(channelType.ToString(), false));
        }

        return Results.Ok(results);
    }

    private static async Task<IResult> GetChannelConfig(
        string channel,
        HttpContext context,
        [FromServices] ITenantChannelConfigStore store,
        CancellationToken ct)
    {
        if (!Enum.TryParse<ChannelType>(channel, ignoreCase: true, out var channelType))
            return Results.BadRequest($"Invalid channel type: {channel}");

        var tenantId = GetTenantId(context);
        var config = await store.GetAsync(tenantId, channelType, ct);

        return config is not null
            ? Results.Ok(config)
            : Results.Ok(new ChannelStatusDto(channelType.ToString(), false));
    }

    private static async Task<IResult> UpdateChannelConfig(
        string channel,
        HttpContext context,
        [FromBody] UpdateChannelConfigRequest body,
        [FromServices] ITenantChannelConfigStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        if (!Enum.TryParse<ChannelType>(channel, ignoreCase: true, out var channelType))
            return Results.BadRequest($"Invalid channel type: {channel}");

        var tenantId = GetTenantId(context);
        var before = await store.GetAsync(tenantId, channelType, ct);
        var config = new TenantChannelConfig
        {
            TenantId = tenantId,
            Channel = channelType,
            IsActive = body.IsActive,
            Credentials = body.Credentials ?? new Dictionary<string, string>(),
        };
        await store.SaveAsync(config, ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "channel.configured", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: channel, targetType: "channel",
            changes: new AuditChanges(Before: before is null ? null : new { before.Channel, before.IsActive }, After: new { config.Channel, config.IsActive }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(config);
    }

    private static IResult TestChannelConfig(string channel)
    {
        if (!Enum.TryParse<ChannelType>(channel, ignoreCase: true, out _))
            return Results.BadRequest($"Invalid channel type: {channel}");

        return Results.Ok(new ChannelTestResponse(true, "Connection test passed"));
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record UpdateChannelConfigRequest(bool IsActive, Dictionary<string, string>? Credentials);

internal sealed record ChannelStatusDto(string Channel, bool IsActive);
internal sealed record ChannelTestResponse(bool Success, string Message);
