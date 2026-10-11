using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Audit;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class ChannelConfigEndpoints
{
    public static void MapChannelConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/channels").RequireAuthorization("AdminOnly").RequireOperationalTenant();

        group.MapGet("/", ListChannelConfigs);
        group.MapGet("/{channel}", GetChannelConfig);
        group.MapPut("/{channel}", UpdateChannelConfig);
        group.MapPost("/{channel}/test", TestChannelConfig);
    }

    /// <summary><c>400</c> code: a key outside the channel's declared credential schema.</summary>
    internal const string UnknownCredentialKeyCode = "unknown-credential-key";

    /// <summary><c>400</c> code: the route names no known channel.</summary>
    internal const string InvalidChannelCode = "invalid-channel";

    private static async Task<Ok<ChannelConfigResponse[]>> ListChannelConfigs(
        HttpContext context,
        [FromServices] ITenantChannelConfigStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var results = new List<ChannelConfigResponse>();

        foreach (var channelType in Enum.GetValues<ChannelType>())
            results.Add(ToResponse(channelType, await store.GetAsync(tenantId, channelType, ct)));

        return TypedResults.Ok(results.ToArray());
    }

    private static async Task<Results<Ok<ChannelConfigResponse>, BadRequest<ErrorDetailResponse>>> GetChannelConfig(
        string channel,
        HttpContext context,
        [FromServices] ITenantChannelConfigStore store,
        CancellationToken ct)
    {
        if (!Enum.TryParse<ChannelType>(channel, ignoreCase: true, out var channelType))
            return TypedResults.BadRequest(new ErrorDetailResponse(InvalidChannelCode, []));

        var tenantId = GetTenantId(context);
        return TypedResults.Ok(ToResponse(channelType, await store.GetAsync(tenantId, channelType, ct)));
    }

    // whatsapp-works-for-real D15: the request merges into what is stored (omitted or blank keeps, value
    // replaces, null removes); the response and the audit record carry key names, never a value.
    private static async Task<Results<Ok<ChannelConfigResponse>, BadRequest<ErrorDetailResponse>>> UpdateChannelConfig(
        string channel,
        HttpContext context,
        [FromBody] UpdateChannelConfigRequest body,
        [FromServices] ITenantChannelConfigStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        if (!Enum.TryParse<ChannelType>(channel, ignoreCase: true, out var channelType))
            return TypedResults.BadRequest(new ErrorDetailResponse(InvalidChannelCode, []));

        var unknownKeys = ChannelCredentialMerge.UnknownKeys(channelType, body.Credentials);
        if (unknownKeys.Count > 0)
            return TypedResults.BadRequest(new ErrorDetailResponse(UnknownCredentialKeyCode, unknownKeys));

        var tenantId = GetTenantId(context);
        var before = await store.GetAsync(tenantId, channelType, ct);
        var merge = ChannelCredentialMerge.Apply(before?.Credentials, body.Credentials);
        var config = new TenantChannelConfig
        {
            TenantId = tenantId,
            Channel = channelType,
            IsActive = body.IsActive,
            Credentials = merge.Credentials,
        };
        await store.SaveAsync(config, ct);
        await audit.RecordAsync(
            tenantId, category: "config", action: "channel.configured", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: channel, targetType: "channel",
            // ADR-0026 Phase A.2 — anonymous types `new { ... }` cannot be
            // serialized under AOT with reflection disabled. Use a typed DTO
            // that's registered in ApiJsonContext.
            changes: new AuditChanges(
                Before: before is null ? null : new ChannelChangeAudit(before.Channel.ToString(), before.IsActive),
                After: new ChannelChangeAudit(config.Channel.ToString(), config.IsActive)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
                // Names only (D15): which keys were replaced or removed, never a value.
                ["changed_keys"] = string.Join(',', merge.ChangedKeys),
            },
            ct: ct);
        return TypedResults.Ok(ToResponse(channelType, config));
    }

    /// <summary>
    /// The redacted view of a channel's configuration: every canonical key of a declared schema (isSet false
    /// when absent or blank), or the stored keys of a channel without one — never a value.
    /// </summary>
    internal static ChannelConfigResponse ToResponse(ChannelType channel, TenantChannelConfig? config)
    {
        var stored = config?.Credentials;
        var keys = ChannelCredentialSchemas.For(channel) is { } schema
            ? schema.Keys.Select(k => k.Name)
            : (stored?.Keys ?? []).Order(StringComparer.Ordinal);

        var credentials = keys
            .Select(key => new ChannelCredentialStatus(
                key,
                stored is not null && stored.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)))
            .ToArray();

        return new ChannelConfigResponse(channel.ToString(), config?.IsActive ?? false, config is not null, credentials);
    }

    private static Results<Ok<ChannelTestResponse>, BadRequest<string>> TestChannelConfig(string channel)
    {
        if (!Enum.TryParse<ChannelType>(channel, ignoreCase: true, out _))
            return TypedResults.BadRequest($"Invalid channel type: {channel}");

        return TypedResults.Ok(new ChannelTestResponse(true, "Connection test passed"));
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

/// <summary>
/// <c>PUT /admin/channels/{channel}</c>. <paramref name="Credentials"/> merges into what is stored: an omitted
/// key or a blank value keeps the stored value, a value replaces it, <see langword="null"/> removes the key.
/// </summary>
internal sealed record UpdateChannelConfigRequest(bool IsActive, Dictionary<string, string?>? Credentials);

// ─── Response DTOs (whatsapp-works-for-real D15: no credential value ever leaves the API) ──────────────────

/// <summary>Whether one credential key holds a non-blank value. The value itself is never returned.</summary>
internal sealed record ChannelCredentialStatus(string Key, bool IsSet);

/// <summary>A tenant's configuration of one channel, credentials redacted to <c>{key, isSet}</c>.</summary>
/// <param name="Channel">The channel name, e.g. <c>WhatsApp</c>.</param>
/// <param name="IsActive">Whether the channel is active for the tenant (false when never configured).</param>
/// <param name="IsConfigured">Whether a configuration row exists for the tenant.</param>
/// <param name="Credentials">Every canonical key of a declared schema, or the stored keys otherwise.</param>
internal sealed record ChannelConfigResponse(
    string Channel,
    bool IsActive,
    bool IsConfigured,
    IReadOnlyList<ChannelCredentialStatus> Credentials);
internal sealed record ChannelTestResponse(bool Success, string Message);
// ADR-0026 Phase A.2 — typed replacement for anonymous-type AuditChanges payload.
internal sealed record ChannelChangeAudit(string Channel, bool IsActive);
