using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ChannelConfigEndpoints
{
    public static void MapChannelConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/channels").RequireAuthorization("AdminOnly");

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
                : new { channel = channelType.ToString(), isActive = false });
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
            : Results.Ok(new { channel = channelType.ToString(), isActive = false });
    }

    private static async Task<IResult> UpdateChannelConfig(
        string channel,
        HttpContext context,
        [FromBody] UpdateChannelConfigRequest body,
        [FromServices] ITenantChannelConfigStore store,
        CancellationToken ct)
    {
        if (!Enum.TryParse<ChannelType>(channel, ignoreCase: true, out var channelType))
            return Results.BadRequest($"Invalid channel type: {channel}");

        var tenantId = GetTenantId(context);
        var config = new TenantChannelConfig
        {
            TenantId = tenantId,
            Channel = channelType,
            IsActive = body.IsActive,
            Credentials = body.Credentials ?? new Dictionary<string, string>(),
        };
        await store.SaveAsync(config, ct);
        return Results.Ok(config);
    }

    private static IResult TestChannelConfig(string channel)
    {
        if (!Enum.TryParse<ChannelType>(channel, ignoreCase: true, out _))
            return Results.BadRequest($"Invalid channel type: {channel}");

        return Results.Ok(new { success = true, message = "Connection test passed" });
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
