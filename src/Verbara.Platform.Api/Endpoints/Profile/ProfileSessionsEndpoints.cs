using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints.Profile;

/// <summary>
/// Profile-scoped active-session listing + revoke surface for the end-user
/// sessions page at <c>/profile/security/sessions</c> (R5.2 PA.2).
/// </summary>
/// <remarks>
/// Behaviourally equivalent to <c>/auth/sessions*</c>; this group exists so the
/// dedicated wizard route can hit a stable, profile-scoped URL without coupling
/// to the legacy auth surface (which also serves login/refresh/logout). The
/// <c>POST /:id/revoke</c> shape (vs <c>DELETE</c>) matches the PA.2 spec and
/// makes the audit-tracked write idiom obvious from the URL.
/// </remarks>
internal static class ProfileSessionsEndpoints
{
    public static void MapProfileSessionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/profile/security/sessions")
            .RequireAuthorization();

        group.MapGet("/", List);
        group.MapPost("/{tokenId}/revoke", Revoke);
    }

    internal static async Task<IResult> List(
        HttpContext context,
        [FromServices] IRefreshTokenStore refreshTokenStore,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var tokens = await refreshTokenStore.GetActiveByUserAsync(tenantId, userId, ct);
        var currentTokenId = await TryGetCurrentTokenIdAsync(context, refreshTokenStore, ct);

        var dtos = tokens
            .Select(t => new ProfileUserSessionDto(
                SessionId: t.TokenId,
                Device: t.UserAgent ?? "Unknown device",
                IpAddress: t.IpAddress ?? "unknown",
                Location: null,
                CreatedAt: t.CreatedAt,
                LastActivity: t.LastActivityAt == default ? t.CreatedAt : t.LastActivityAt,
                IsCurrentSession: t.TokenId == currentTokenId))
            .ToArray();

        return Results.Ok(dtos);
    }

    internal static async Task<IResult> Revoke(
        string tokenId,
        HttpContext context,
        [FromServices] IRefreshTokenStore refreshTokenStore,
        [FromServices] IAuditService audit,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var currentTokenId = await TryGetCurrentTokenIdAsync(context, refreshTokenStore, ct);
        if (tokenId == currentTokenId)
            return Results.BadRequest(new ErrorResponse("Cannot revoke the current session — sign out instead."));

        var tokens = await refreshTokenStore.GetActiveByUserAsync(tenantId, userId, ct);
        var target = tokens.FirstOrDefault(t => t.TokenId == tokenId);
        if (target is null)
            return Results.NotFound();

        await refreshTokenStore.RevokeAsync(tokenId, DateTimeOffset.UtcNow, null, ct);

        await audit.RecordAsync(
            new Core.TenantId(tenantId),
            category: "auth",
            action: "user.session.revoked",
            severity: "info",
            actorId: userId,
            actorType: "user",
            targetId: tokenId,
            targetType: "refresh_token",
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["actor_tenant_id"] = tenantId,
                ["ip"] = GetIpAddress(context) ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.SessionRevoked,
            GetIpAddress(context), GetUserAgent(context),
            new Dictionary<string, string> { ["session_id"] = tokenId }, ct);

        return Results.NoContent();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static (string? TenantId, string? UserId) GetAuthClaims(HttpContext context)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        var userId = context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return (tenantId, userId);
    }

    private static async Task<string?> TryGetCurrentTokenIdAsync(
        HttpContext context,
        IRefreshTokenStore refreshTokenStore,
        CancellationToken ct)
    {
        if (!context.Request.Cookies.TryGetValue("refresh_token", out var refreshToken) ||
            string.IsNullOrEmpty(refreshToken))
            return null;

        var hash = RefreshTokenService.HashToken(refreshToken);
        var stored = await refreshTokenStore.GetByHashAsync(hash, ct);
        return stored?.TokenId;
    }

    private static string? GetIpAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    private static string? GetUserAgent(HttpContext context) =>
        context.Request.Headers.UserAgent.FirstOrDefault();
}

/// <summary>
/// Profile-scoped active-session DTO (R5.2 PA.2). Mirrors the legacy
/// <c>UserSessionDto</c> but ships the additional <c>LastActivity</c> +
/// <c>Location</c> fields the new sessions page renders.
/// </summary>
internal sealed record ProfileUserSessionDto(
    string SessionId,
    string Device,
    string IpAddress,
    string? Location,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivity,
    bool IsCurrentSession);
