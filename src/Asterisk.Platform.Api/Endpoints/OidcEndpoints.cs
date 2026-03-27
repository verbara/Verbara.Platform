using System.Security.Claims;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Endpoints;

internal static class OidcEndpoints
{
    public static void MapOidcEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/oidc");

        group.MapGet("/login", OidcLogin).AllowAnonymous();
        group.MapGet("/callback", OidcCallback).AllowAnonymous();
        group.MapPost("/logout", OidcLogout).RequireAuthorization();
    }

    private static async Task<IResult> OidcLogin(
        HttpContext context,
        ITenantAuthConfigStore configStore,
        string? tenant_id,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant_id))
            return Results.BadRequest(new { error = "tenant_id query parameter is required" });

        var config = await configStore.GetAsync(tenant_id, ct);
        if (config is null || !config.OidcEnabled || string.IsNullOrEmpty(config.OidcAuthority))
            return Results.BadRequest(new { error = "OIDC is not enabled for this tenant" });

        // Build the OIDC authorization URL
        var redirectUri = $"{context.Request.Scheme}://{context.Request.Host}/api/auth/oidc/callback";
        var authorizationUrl = $"{config.OidcAuthority.TrimEnd('/')}/authorize" +
            $"?client_id={Uri.EscapeDataString(config.OidcClientId ?? "")}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope=openid%20profile%20email" +
            $"&state={Uri.EscapeDataString(tenant_id)}";

        return Results.Redirect(authorizationUrl);
    }

    private static async Task<IResult> OidcCallback(
        HttpContext context,
        ITenantAuthConfigStore configStore,
        IUserStore userStore,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
        AuthEventService authEvents,
        string? state,
        string? code,
        string? error,
        CancellationToken ct)
    {
        // state carries the tenant_id
        if (string.IsNullOrWhiteSpace(state))
            return Results.BadRequest(new { error = "Missing tenant_id (state parameter)" });

        var tenantId = state;

        if (!string.IsNullOrEmpty(error))
            return Results.BadRequest(new { error = $"OIDC provider error: {error}" });

        if (string.IsNullOrEmpty(code))
            return Results.BadRequest(new { error = "Missing authorization code" });

        var config = await configStore.GetAsync(tenantId, ct);
        if (config is null || !config.OidcEnabled)
            return Results.BadRequest(new { error = "OIDC is not enabled for this tenant" });

        // In a production implementation, we would:
        // 1. Exchange the authorization code for tokens via the IdP's token endpoint
        // 2. Validate the ID token
        // 3. Extract user claims (email, name, etc.)
        // For now, return an error since we can't complete the flow without a real IdP
        return Results.BadRequest(new { error = "OIDC token exchange not yet implemented" });
    }

    private static async Task<IResult> OidcLogout(
        HttpContext context,
        RefreshTokenService refreshService,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        var userId = context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // Revoke refresh token if present
        var rawToken = context.Request.Cookies["refresh_token"];
        if (!string.IsNullOrEmpty(rawToken))
            await refreshService.RevokeAsync(rawToken, ct);

        if (tenantId is not null)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString();
            var ua = context.Request.Headers.UserAgent.FirstOrDefault();
            await authEvents.LogAsync(tenantId, userId, "oidc_logout", ip, ua, null, ct);
        }

        context.Response.Cookies.Delete("refresh_token");
        return Results.Ok(new { message = "Logged out" });
    }
}
