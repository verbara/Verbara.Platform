using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class AuthAdminEndpoints
{
    public static void MapAuthAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/auth")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/config", GetConfig);
        group.MapPut("/config", UpdateConfig);
        group.MapGet("/events", ListEvents);
        group.MapGet("/sessions", ListSessions);
        group.MapDelete("/sessions/{id}", RevokeSession);
        group.MapDelete("/sessions/by-user/{userId}", RevokeAllUserSessions);
    }

    private static async Task<Results<Ok<TenantAuthConfigResponse>, UnauthorizedHttpResult>> GetConfig(
        HttpContext context,
        [FromServices] ITenantAuthConfigStore configStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (tenantId is null)
            return TypedResults.Unauthorized();

        var config = await configStore.GetAsync(tenantId, ct)
            ?? new TenantAuthConfig { TenantId = tenantId };

        // ADMIN-001 (PREPUB-2026-05-09): project to redacted response so the
        // OIDC client secret never leaves the process via HTTP.
        return TypedResults.Ok(TenantAuthConfigResponse.FromConfig(config));
    }

    private static async Task<Results<Ok<TenantAuthConfigResponse>, UnauthorizedHttpResult>> UpdateConfig(
        [FromBody] UpdateTenantAuthConfigRequest body,
        HttpContext context,
        [FromServices] ITenantAuthConfigStore configStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (tenantId is null)
            return TypedResults.Unauthorized();

        var config = await configStore.GetAsync(tenantId, ct)
            ?? new TenantAuthConfig { TenantId = tenantId };

        // Partial update — only set fields that are provided
        if (body.MfaPolicy is not null) config.MfaPolicy = body.MfaPolicy;
        if (body.MfaRequiredRoles is not null) config.MfaRequiredRoles = body.MfaRequiredRoles;
        if (body.PasswordMinLength.HasValue) config.PasswordMinLength = body.PasswordMinLength.Value;
        if (body.PasswordRequireUppercase.HasValue) config.PasswordRequireUppercase = body.PasswordRequireUppercase.Value;
        if (body.PasswordRequireNumber.HasValue) config.PasswordRequireNumber = body.PasswordRequireNumber.Value;
        if (body.PasswordRequireSpecial.HasValue) config.PasswordRequireSpecial = body.PasswordRequireSpecial.Value;
        if (body.LockoutThreshold.HasValue) config.LockoutThreshold = body.LockoutThreshold.Value;
        if (body.LockoutDurationMinutes.HasValue) config.LockoutDurationMinutes = body.LockoutDurationMinutes.Value;
        if (body.SessionIdleTimeoutMinutes.HasValue) config.SessionIdleTimeoutMinutes = body.SessionIdleTimeoutMinutes.Value;
        if (body.SessionAbsoluteTimeoutHours.HasValue) config.SessionAbsoluteTimeoutHours = body.SessionAbsoluteTimeoutHours.Value;
        if (body.PendingPauseTimeoutMinutes.HasValue) config.PendingPauseTimeoutMinutes = body.PendingPauseTimeoutMinutes.Value;
        if (body.OidcEnabled.HasValue) config.OidcEnabled = body.OidcEnabled.Value;
        if (body.OidcAuthority is not null) config.OidcAuthority = body.OidcAuthority;
        if (body.OidcClientId is not null) config.OidcClientId = body.OidcClientId;
        if (body.OidcClientSecret is not null) config.OidcClientSecret = body.OidcClientSecret;
        if (body.OidcAutoCreateUsers.HasValue) config.OidcAutoCreateUsers = body.OidcAutoCreateUsers.Value;
        if (body.OidcDefaultRole is not null) config.OidcDefaultRole = body.OidcDefaultRole;

        config.UpdatedAt = DateTimeOffset.UtcNow;
        await configStore.SaveAsync(config, ct);

        // ADMIN-001 (PREPUB-2026-05-09): project to redacted response so the
        // OIDC client secret never leaves the process via HTTP — applies to
        // PUT-after-write as well as GET reads.
        return TypedResults.Ok(TenantAuthConfigResponse.FromConfig(config));
    }

    private static async Task<Results<Ok<PagedResult<AuthEvent>>, UnauthorizedHttpResult>> ListEvents(
        HttpContext context,
        [FromServices] AuthEventService authEvents,
        int page = 1,
        int pageSize = 50,
        string? userId = null,
        string? eventType = null,
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        if (tenantId is null)
            return TypedResults.Unauthorized();

        if (endDate.HasValue && endDate.Value.TimeOfDay == TimeSpan.Zero)
            endDate = endDate.Value.AddDays(1).AddTicks(-1);

        var query = new AuthEventQuery(
            UserId: userId,
            EventType: eventType,
            StartDate: startDate,
            EndDate: endDate,
            Page: page,
            PageSize: pageSize);

        return TypedResults.Ok(await authEvents.SearchAsync(tenantId, query, ct));
    }

    private static async Task<Results<Ok<IReadOnlyList<ActiveSession>>, UnauthorizedHttpResult>> ListSessions(
        HttpContext context,
        SessionService sessionService,
        string? userId,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (tenantId is null)
            return TypedResults.Unauthorized();

        var sessions = string.IsNullOrWhiteSpace(userId)
            ? await sessionService.ListAllActiveSessionsAsync(tenantId, ct)
            : await sessionService.ListActiveSessionsAsync(tenantId, userId, ct);

        return TypedResults.Ok(sessions);
    }

    private static async Task<IResult> RevokeSession(
        string id,
        HttpContext context,
        SessionService sessionService,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var adminUserId = GetUserId(context);
        if (tenantId is null)
            return Results.Unauthorized();

        var ip = context.Connection.RemoteIpAddress?.ToString();
        var ua = context.Request.Headers.UserAgent.FirstOrDefault();

        await sessionService.RevokeSessionAsync(tenantId, adminUserId ?? "admin", id, ip, ua, ct);
        return Results.NoContent();
    }

    private static async Task<Results<Ok<RevokedSessionsResponse>, UnauthorizedHttpResult>> RevokeAllUserSessions(
        string userId,
        HttpContext context,
        [FromServices] SessionService sessionService,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var adminUserId = GetUserId(context);
        if (tenantId is null)
            return TypedResults.Unauthorized();

        var ip = context.Connection.RemoteIpAddress?.ToString();
        var ua = context.Request.Headers.UserAgent.FirstOrDefault();

        var count = await sessionService.RevokeAllSessionsForUserAsync(
            tenantId, adminUserId ?? "admin", userId, ip, ua, ct);

        return TypedResults.Ok(new RevokedSessionsResponse(count));
    }

    private static string? GetTenantId(HttpContext context) =>
        context.User.FindFirst("tid")?.Value
        ?? context.User.FindFirst("tenant_id")?.Value;

    private static string? GetUserId(HttpContext context) =>
        context.User.FindFirst("user_id")?.Value
        ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record RevokedSessionsResponse(int RevokedCount);

internal sealed record UpdateTenantAuthConfigRequest
{
    public string? MfaPolicy { get; init; }
    public IReadOnlyList<string>? MfaRequiredRoles { get; init; }
    public int? PasswordMinLength { get; init; }
    public bool? PasswordRequireUppercase { get; init; }
    public bool? PasswordRequireNumber { get; init; }
    public bool? PasswordRequireSpecial { get; init; }
    public int? LockoutThreshold { get; init; }
    public int? LockoutDurationMinutes { get; init; }
    public int? SessionIdleTimeoutMinutes { get; init; }
    public int? SessionAbsoluteTimeoutHours { get; init; }
    public int? PendingPauseTimeoutMinutes { get; init; }
    public bool? OidcEnabled { get; init; }
    public string? OidcAuthority { get; init; }
    public string? OidcClientId { get; init; }
    public string? OidcClientSecret { get; init; }
    public bool? OidcAutoCreateUsers { get; init; }
    public string? OidcDefaultRole { get; init; }
}
