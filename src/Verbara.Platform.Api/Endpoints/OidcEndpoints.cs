using System.Security.Claims;
using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Identity.OidcTokenExchange;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class OidcEndpoints
{
    private const string StateCookieName = "oidc_state";
    private const string ProtectorPurpose = "OidcFlowState";
    private static readonly TimeSpan FlowTimeout = TimeSpan.FromMinutes(5);

    public static void MapOidcEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth/oidc")
            .RequirePlanFeature(PlanFeature.OidcSso);

        group.MapGet("/login", OidcLogin).AllowAnonymous();
        group.MapGet("/callback", OidcCallback).AllowAnonymous();
        group.MapPost("/logout", OidcLogout).RequireAuthorization();
    }

    private static async Task<IResult> OidcLogin(
        HttpContext context,
        [FromServices] ITenantAuthConfigStore configStore,
        [FromServices] IDataProtectionProvider dataProtection,
        string? tenant_id,
        string? return_url,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant_id))
            return Results.BadRequest(new ErrorResponse("tenant_id query parameter is required"));

        var config = await configStore.GetAsync(tenant_id, ct);
        if (config is null || !config.OidcEnabled || string.IsNullOrEmpty(config.OidcAuthority))
            return Results.BadRequest(new ErrorResponse("OIDC is not enabled for this tenant"));

        var codeVerifier = OidcTokenExchangeService.GenerateCodeVerifier();
        var codeChallenge = OidcTokenExchangeService.ComputeCodeChallenge(codeVerifier);
        var nonce = OidcTokenExchangeService.GenerateNonce();
        var state = OidcTokenExchangeService.GenerateNonce();

        var flowState = new OidcFlowState
        {
            CodeVerifier = codeVerifier,
            Nonce = nonce,
            TenantId = tenant_id,
            ReturnUrl = return_url,
            ExpiresAtUnix = DateTimeOffset.UtcNow.Add(FlowTimeout).ToUnixTimeSeconds(),
        };

        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        var serialized = JsonSerializer.Serialize(flowState, OidcJsonContext.Default.OidcFlowState);
        var encrypted = protector.Protect(serialized);

        context.Response.Cookies.Append(StateCookieName, encrypted, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/api/auth/oidc",
            MaxAge = FlowTimeout,
        });

        var redirectUri = $"{context.Request.Scheme}://{context.Request.Host}/api/auth/oidc/callback";
        var authorizationUrl = $"{config.OidcAuthority.TrimEnd('/')}/authorize" +
            $"?client_id={Uri.EscapeDataString(config.OidcClientId ?? "")}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope=openid%20profile%20email" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&nonce={Uri.EscapeDataString(nonce)}" +
            $"&state={Uri.EscapeDataString(state)}";

        return Results.Redirect(authorizationUrl);
    }

    private static async Task<IResult> OidcCallback(
        HttpContext context,
        [FromServices] ITenantAuthConfigStore configStore,
        [FromServices] IUserStore userStore,
        [FromServices] IOidcTokenExchangeService tokenExchange,
        [FromServices] IOidcUserProvisioningService provisioning,
        [FromServices] IDataProtectionProvider dataProtection,
        [FromServices] IMfaPolicyEvaluator mfaEvaluator,
        [FromServices] IMfaPendingCache mfaCache,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
        AuthEventService authEvents,
        string? code,
        string? error,
        string? error_description,
        CancellationToken ct)
    {
        var (flowState, stateError) = DecryptFlowState(context, dataProtection);
        if (stateError is not null)
            return stateError;

        var tenantId = flowState!.TenantId;
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var ua = context.Request.Headers.UserAgent.FirstOrDefault();

        if (!string.IsNullOrEmpty(error))
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua, null, ct);
            var errorMsg = string.IsNullOrEmpty(error_description)
                ? $"OIDC provider error: {error}"
                : $"OIDC provider error: {error} — {error_description}";
            return Results.BadRequest(new ErrorResponse(errorMsg));
        }

        if (string.IsNullOrEmpty(code))
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua, null, ct);
            return Results.BadRequest(new ErrorResponse("Missing authorization code"));
        }

        var config = await configStore.GetAsync(tenantId, ct);
        if (config is null || !config.OidcEnabled)
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua, null, ct);
            return Results.BadRequest(new ErrorResponse("OIDC is not enabled for this tenant"));
        }

        var redirectUri = $"{context.Request.Scheme}://{context.Request.Host}/api/auth/oidc/callback";

        OidcTokenResponse tokenResponse;
        try
        {
            tokenResponse = await tokenExchange.ExchangeCodeAsync(
                config.OidcAuthority!, code, flowState.CodeVerifier,
                redirectUri, config.OidcClientId ?? "", config.OidcClientSecret ?? "", ct);
        }
        catch (Exception ex)
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua, null, ct);
            return Results.BadRequest(new ErrorResponse($"OIDC token exchange failed: {ex.Message}"));
        }

        OidcClaimsResult claims;
        try
        {
            claims = await tokenExchange.ValidateIdTokenAsync(
                tokenResponse.IdToken, config.OidcAuthority!,
                config.OidcClientId ?? "", flowState.Nonce, ct);
        }
        catch (Exception ex)
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua, null, ct);
            return Results.BadRequest(new ErrorResponse($"OIDC ID token validation failed: {ex.Message}"));
        }

        var user = await provisioning.ProvisionOrUpdateAsync(tenantId, claims, config, ct);
        if (user is null)
        {
            await authEvents.LogAsync(tenantId, null, AuthEventTypes.OidcLoginFailure, ip, ua, null, ct);
            return Results.Unauthorized();
        }

        if (user.Status != UserStatus.Active)
        {
            await authEvents.LogAsync(tenantId, user.UserId.Value, AuthEventTypes.OidcLoginFailure, ip, ua, null, ct);
            return Results.Unauthorized();
        }

        return await CompleteOidcLoginAsync(context, jwtService, refreshService, authEvents, mfaEvaluator, mfaCache, user, flowState, ip, ua, ct);
    }

    private static (OidcFlowState? State, IResult? Error) DecryptFlowState(
        HttpContext context, IDataProtectionProvider dataProtection)
    {
        var encryptedState = context.Request.Cookies[StateCookieName];
        context.Response.Cookies.Delete(StateCookieName, new CookieOptions { Path = "/api/auth/oidc" });

        if (string.IsNullOrEmpty(encryptedState))
            return (null, Results.BadRequest(new ErrorResponse("Missing OIDC state cookie — flow may have expired")));

        OidcFlowState flowState;
        try
        {
            var protector = dataProtection.CreateProtector(ProtectorPurpose);
            var decrypted = protector.Unprotect(encryptedState);
            flowState = JsonSerializer.Deserialize(decrypted, OidcJsonContext.Default.OidcFlowState)
                ?? throw new InvalidOperationException("Null deserialization");
        }
        catch
        {
            return (null, Results.BadRequest(new ErrorResponse("Invalid OIDC state cookie — possible tampering")));
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > flowState.ExpiresAtUnix)
            return (null, Results.BadRequest(new ErrorResponse("OIDC flow expired — please try again")));

        return (flowState, null);
    }

    // Visibility elevated to internal so Verbara.Platform.Api.Tests (InternalsVisibleTo)
    // can directly exercise the MFA gate in unit tests without spinning up WebApplicationFactory.
    internal static async Task<IResult> CompleteOidcLoginAsync(
        HttpContext context, JwtTokenService jwtService, RefreshTokenService refreshService,
        AuthEventService authEvents, IMfaPolicyEvaluator mfaEvaluator, IMfaPendingCache mfaCache,
        User user, OidcFlowState flowState, string? ip, string? ua, CancellationToken ct)
    {
        var tenantId = flowState.TenantId;

        // v1.9.2 Frente C — P0: enforce tenant MFA policy on OIDC callback.
        // OIDC SSO was previously exempt from MFA policy enforcement that already
        // applied to password login, API-key login, and refresh. An attacker who
        // could authenticate via an OIDC provider bypassed the tenant's
        // required_all / required_for_roles policy entirely.
        var policyRequiresMfa = await mfaEvaluator.RequiresMfaAsync(tenantId, user.Role, ct);

        // Case A: policy requires MFA + user not enrolled → redirect to enrollment-required fragment.
        // The frontend intercepts #oidc_mfa_enrollment_required and drives the enrollment wizard.
        if (policyRequiresMfa && !user.MfaEnabled)
        {
            await authEvents.LogAsync(tenantId, user.UserId.Value, AuthEventTypes.OidcLoginFailure, ip, ua,
                new Dictionary<string, string> { ["reason"] = "mfa_enrollment_required" }, ct);
            var enrollReturnUrl = flowState.ReturnUrl ?? "/";
            return Results.Redirect(
                $"{enrollReturnUrl}#oidc_mfa_enrollment_required" +
                $"&tenant_id={Uri.EscapeDataString(tenantId)}" +
                $"&email={Uri.EscapeDataString(user.Email)}");
        }

        // Case B: user has MFA enrolled → create pending challenge + redirect.
        // The frontend intercepts #oidc_mfa_challenge and POSTs to /auth/mfa/verify.
        if (user.MfaEnabled)
        {
            var challengeToken = await AuthEndpoints.GenerateMfaChallengeTokenAndStoreAsync(
                user.UserId.Value, tenantId, mfaCache, ct);
            var challengeReturnUrl = flowState.ReturnUrl ?? "/";
            return Results.Redirect(
                $"{challengeReturnUrl}#oidc_mfa_challenge" +
                $"&challenge_token={Uri.EscapeDataString(challengeToken)}" +
                $"&tenant_id={Uri.EscapeDataString(tenantId)}");
        }

        // Case C: no MFA required and user not enrolled → existing token issuance (unchanged).
        IReadOnlySet<string>? permissions = null;
        var resolver = context.RequestServices.GetService<PermissionResolver>();
        if (resolver is not null)
        {
            try { permissions = await resolver.ResolveAsync(user.TenantId, user.UserId, ct); }
            catch { /* permissions resolved at authorization time */ }
        }

        var (accessToken, expiresAt) = jwtService.GenerateAccessToken(user, permissions);
        var (rawRefreshToken, _) = await refreshService.GenerateAsync(
            user.UserId.Value, user.TenantId.Value, ip, ua, ct);

        context.Response.Cookies.Append("refresh_token", rawRefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            MaxAge = TimeSpan.FromDays(7),
        });

        await authEvents.LogAsync(tenantId, user.UserId.Value, AuthEventTypes.OidcLoginSuccess, ip, ua, null, ct);

        var returnUrl = flowState.ReturnUrl ?? "/";
        var callbackUrl = $"{returnUrl}#oidc_callback" +
            $"&access_token={Uri.EscapeDataString(accessToken)}" +
            $"&expires_at={Uri.EscapeDataString(expiresAt.ToString("O"))}" +
            $"&tenant_id={Uri.EscapeDataString(tenantId)}" +
            $"&user_id={Uri.EscapeDataString(user.UserId.Value)}" +
            $"&email={Uri.EscapeDataString(user.Email)}" +
            $"&display_name={Uri.EscapeDataString(user.DisplayName)}" +
            $"&role={Uri.EscapeDataString(user.Role.ToString().ToLowerInvariant())}";

        return Results.Redirect(callbackUrl);
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
        return Results.Ok(new MessageResponse("Logged out"));
    }
}
