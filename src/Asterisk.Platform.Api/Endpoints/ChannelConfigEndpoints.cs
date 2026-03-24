using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ChannelConfigEndpoints
{
    public static void MapChannelConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/channels").RequireAuthorization("AdminOnly");

        group.MapGet("/{channel}", GetChannelConfig);
        group.MapPut("/{channel}", UpdateChannelConfig);
    }

    private static async Task<IResult> GetChannelConfig(
        string channel,
        HttpContext context,
        ITenantChannelConfigStore store,
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
        UpdateChannelConfigRequest body,
        ITenantChannelConfigStore store,
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

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record UpdateChannelConfigRequest(bool IsActive, Dictionary<string, string>? Credentials);
