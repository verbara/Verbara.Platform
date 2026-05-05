using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Endpoints;

internal static class AuthEndpoints
{
    private const string RefreshCookieName = "refresh_token";
    private static readonly TimeSpan MfaPendingTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PasswordResetTtl = TimeSpan.FromHours(1);


    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth");

        // Anonymous auth endpoints
        group.MapPost("/login", Login).AllowAnonymous();
        group.MapPost("/refresh", Refresh).AllowAnonymous();
        group.MapPost("/login/apikey", ApiKeyLogin).AllowAnonymous();
        group.MapPost("/forgot-password", ForgotPassword).AllowAnonymous();
        group.MapPost("/reset-password", ResetPassword).AllowAnonymous();

        // MFA verification during login (anonymous — uses challenge token)
        group.MapPost("/mfa/verify", MfaVerify).AllowAnonymous();

        // Authenticated endpoints
        group.MapPost("/logout", Logout).RequireAuthorization();
        group.MapPost("/change-password", ChangePassword).RequireAuthorization();

        // MFA management (authenticated)
        group.MapPost("/mfa/setup", MfaSetup).RequireAuthorization();
        group.MapPost("/mfa/confirm", MfaConfirm).RequireAuthorization();
        group.MapDelete("/mfa", MfaDisable).RequireAuthorization();

        // Sub C T2.2a: regenerate recovery codes post-enrollment
        group.MapPost("/mfa/recovery-codes/regenerate", RegenerateRecoveryCodes).RequireAuthorization();

        // Sub C T2.1a: user-scoped sessions management
        group.MapGet("/sessions", GetOwnSessions).RequireAuthorization();
        group.MapDelete("/sessions/{tokenId}", RevokeOwnSession).RequireAuthorization();
        group.MapPost("/sessions/revoke-others", RevokeOtherSessions).RequireAuthorization();

        // Sub C T2.3a: expose tenant password policy for frontend validation
        group.MapGet("/password-policy", GetPasswordPolicy).RequireAuthorization();
    }

    // ─── Login ──────────────────────────────────────────────────────────────────

    // Visibility elevated from `private` to `internal` so the Api.Tests project
    // (which has InternalsVisibleTo) can invoke this handler directly for MFA
    // policy enforcement regression tests (v1.9.0 P0 security fix).
    internal static async Task<IResult> Login(
        [FromBody] LoginRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        AccountLockoutService lockoutService,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
        AuthEventService authEvents,
        [FromServices] ITenantAuthConfigStore configStore,
        [FromServices] IMfaPolicyEvaluator mfaPolicyEvaluator,
        [FromServices] IMfaPendingCache mfaCache,
        // AHH Phase 4 — optional, used only on the on-login Argon2id rehash
        // path. When the queue is not registered (tests / single-process
        // bootstrap), the rehash is skipped silently and the legacy BCrypt
        // hash stays in the user row until the next deploy that wires the
        // queue. The migration is bounded by the user's login activity, not
        // calendar time.
        [FromServices] AuthWriteQueue? authWriteQueue,
        CancellationToken ct)
    {
        // Resolve tenant: body > middleware context (header/subdomain)
        var rawTenantId = body.TenantId;
        if (string.IsNullOrWhiteSpace(rawTenantId) && context.Items.TryGetValue("TenantId", out var ctxTenant))
            rawTenantId = ctxTenant?.ToString();

        if (string.IsNullOrWhiteSpace(rawTenantId))
            return Results.BadRequest(new ErrorResponse("Tenant identification required. Provide tenantId in body or X-Tenant-Id header."));

        var tenantId = new TenantId(rawTenantId);
        var ip = GetIpAddress(context);
        var ua = GetUserAgent(context);

        var user = await userStore.GetByEmailAsync(tenantId, body.Email, ct);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            await authEvents.LogAsync(rawTenantId!, null, AuthEventTypes.LoginFailure, ip, ua,
                new Dictionary<string, string> { ["email"] = body.Email, ["reason"] = "invalid_credentials" }, ct);
            return Results.Unauthorized();
        }

        if (user.IsLockedOut(DateTimeOffset.UtcNow))
            return Results.Json(new ErrorResponse("Account is locked"),
                ApiJsonContext.Default.ErrorResponse, statusCode: 423);

        if (!PasswordService.VerifyPassword(body.Password, user.PasswordHash))
        {
            await lockoutService.RecordFailedAttemptAsync(user, ip, ua, ct);
            await authEvents.LogAsync(rawTenantId!, user.UserId.Value, AuthEventTypes.LoginFailure, ip, ua,
                new Dictionary<string, string> { ["reason"] = "invalid_password" }, ct);
            return Results.Unauthorized();
        }

        // AHH Phase 4 — on successful login, if the stored hash is still
        // legacy BCrypt, recompute it as Argon2id and enqueue the upsert.
        // The hash is computed synchronously inside the request to avoid
        // putting the plaintext password on the queue. ~30 ms one-shot per
        // user; subsequent logins for that user verify Argon2id (~33 ms vs
        // BCrypt12's ~162 ms — the dominant Phase 4 perf win).
        if (authWriteQueue is not null && PasswordService.IsBcryptHash(user.PasswordHash))
        {
            var newHash = PasswordService.HashPassword(body.Password);
            authWriteQueue.TryEnqueue(new PasswordRehashCommand(
                user.TenantId.Value, user.UserId.Value, newHash));
        }

        // v1.9.0 P0 — Tenant MFA policy enforcement (Gap 1).
        // If the tenant's policy requires MFA for this user's role AND the user
        // has not enrolled (no TOTP secret confirmed), block with 401 + an
        // explicit "mfa_enrollment_required" signal so the frontend can drive
        // the enrollment flow. User with MfaEnabled=true falls through to the
        // existing per-user challenge — the policy check does NOT duplicate it.
        var policyRequiresMfa = await mfaPolicyEvaluator.RequiresMfaAsync(rawTenantId!, user.Role, ct);
        if (policyRequiresMfa && !user.MfaEnabled)
        {
            await authEvents.LogAsync(rawTenantId!, user.UserId.Value, AuthEventTypes.LoginFailure, ip, ua,
                new Dictionary<string, string> { ["reason"] = "mfa_enrollment_required" }, ct);
            return Results.Json(
                new MfaEnrollmentRequiredResponse(
                    MfaEnrollmentRequired: true,
                    Reason: "Your organization requires multi-factor authentication for this account. MFA enrollment must be completed before access can be granted."),
                ApiJsonContext.Default.MfaEnrollmentRequiredResponse,
                statusCode: 401);
        }

        // Check per-user MFA (existing behavior — preserved)
        if (user.MfaEnabled)
        {
            var challengeToken = GenerateToken();
            await mfaCache.StoreAsync(challengeToken, new MfaPendingEntry
            {
                UserId = user.UserId.Value,
                TenantId = rawTenantId!,
                ExpiresAt = DateTimeOffset.UtcNow.Add(MfaPendingTtl),
            }, ct);

            return Results.Ok(new MfaChallengeResponse(true, challengeToken));
        }

        // Issue tokens
        return await IssueTokensAsync(user, context, jwtService, refreshService, lockoutService, authEvents, ct);
    }

    // ─── MFA Verify ──────────────────────────────────────────────────────────────

    private static async Task<IResult> MfaVerify(
        [FromBody] MfaVerifyRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
        AccountLockoutService lockoutService,
        AuthEventService authEvents,
        [FromServices] IMfaPendingCache mfaCache,
        CancellationToken ct)
    {
        var pending = await mfaCache.TakeAsync(body.MfaToken, ct);
        if (pending is null)
            return Results.BadRequest(new ErrorResponse("Invalid or expired challenge token"));

        var tenantId = new TenantId(pending.TenantId);
        var user = await userStore.GetByIdAsync(tenantId, EntityId.From(pending.UserId), ct);
        if (user is null)
            return Results.Unauthorized();

        // Try TOTP code first, then recovery code
        var verified = false;
        if (!string.IsNullOrEmpty(body.Code) && !string.IsNullOrEmpty(user.MfaSecret))
        {
            verified = MfaService.VerifyCode(user.MfaSecret, body.Code);
        }

        if (!verified && !string.IsNullOrEmpty(body.RecoveryCode) && user.MfaRecoveryCodes is { Count: > 0 })
        {
            var (isValid, index) = MfaService.ValidateRecoveryCode(body.RecoveryCode, user.MfaRecoveryCodes);
            if (isValid)
            {
                // Remove used recovery code
                var codes = user.MfaRecoveryCodes.ToList();
                codes.RemoveAt(index);
                user.MfaRecoveryCodes = codes;
                await userStore.SaveAsync(user, ct);
                verified = true;
            }
        }

        if (!verified)
            return Results.Unauthorized();

        return await IssueTokensAsync(user, context, jwtService, refreshService, lockoutService, authEvents, ct);
    }

    // ─── Refresh ────────────────────────────────────────────────────────────────

    // Visibility elevated from `private` to `internal` so the Api.Tests project
    // (which has InternalsVisibleTo) can invoke this handler directly for MFA
    // policy enforcement regression tests (v1.9.0 P0 security fix).
    internal static async Task<IResult> Refresh(
        HttpContext context,
        [FromServices] IUserStore userStore,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
        [FromServices] IRefreshTokenStore refreshTokenStore,
        [FromServices] ITenantAuthConfigStore configStore,
        [FromServices] IMfaPolicyEvaluator mfaPolicyEvaluator,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var rawToken = context.Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(rawToken))
            return Results.Unauthorized();

        var ip = GetIpAddress(context);
        var ua = GetUserAgent(context);

        var result = await refreshService.RotateAsync(rawToken, ip, ua, ct);
        if (result is null)
            return Results.Unauthorized();

        var (newRawToken, newStoredToken) = result.Value;
        var user = await userStore.GetByIdAsync(
            new TenantId(newStoredToken.TenantId),
            EntityId.From(newStoredToken.UserId),
            ct);

        if (user is null)
            return Results.Unauthorized();

        // v1.9.0 P0 — Tenant MFA policy re-evaluation on refresh (Gap 2).
        // If tenant admin flipped the policy (optional → required_all, or added
        // this role to MfaRequiredRoles) AFTER this refresh-token was issued
        // and the user still has not enrolled, we MUST invalidate the whole
        // refresh-token lineage for this user and force re-authentication. The
        // original raw token was already revoked by RotateAsync — we also revoke
        // the freshly-minted replacement + all other active tokens so a lingering
        // attacker cannot keep rotating.
        var policyRequiresMfa = await mfaPolicyEvaluator.RequiresMfaAsync(user.TenantId.Value, user.Role, ct);
        if (policyRequiresMfa && !user.MfaEnabled)
        {
            await refreshTokenStore.RevokeAllForUserAsync(
                user.TenantId.Value, user.UserId.Value, DateTimeOffset.UtcNow, ct);
            context.Response.Cookies.Delete(RefreshCookieName);
            await authEvents.LogAsync(user.TenantId.Value, user.UserId.Value,
                AuthEventTypes.SessionRevoked, ip, ua,
                new Dictionary<string, string> { ["reason"] = "mfa_policy_updated" }, ct);
            return Results.Json(
                new MfaEnrollmentRequiredResponse(
                    MfaEnrollmentRequired: true,
                    Reason: "Re-authentication required: your organization's MFA policy has changed and this account must complete MFA enrollment."),
                ApiJsonContext.Default.MfaEnrollmentRequiredResponse,
                statusCode: 401);
        }

        // Resolve granular permissions for refreshed JWT
        IReadOnlySet<string>? permissions = null;
        var resolver = context.RequestServices.GetService<PermissionResolver>();
        if (resolver is not null)
        {
            try { permissions = await resolver.ResolveAsync(user.TenantId, user.UserId, ct); }
            catch { /* permissions will be resolved at authorization time instead */ }
        }

        var (accessToken, expiresAt) = jwtService.GenerateAccessToken(user, permissions);
        SetRefreshCookie(context, newRawToken);

        var featureRegistry = context.RequestServices.GetService<IFeatureRegistry>();
        var features = new Dictionary<string, bool>(featureRegistry?.GetFeatures() ?? new Dictionary<string, bool>());

        return Results.Ok(new TokenResponse(
            accessToken,
            expiresAt,
            new TokenUserDto(user.UserId.Value, user.Email, user.DisplayName, user.Role.ToString().ToLowerInvariant()),
            user.TenantId.Value,
            permissions?.ToArray() ?? [],
            features));
    }

    // ─── Logout ─────────────────────────────────────────────────────────────────

    private static async Task<IResult> Logout(
        HttpContext context,
        RefreshTokenService refreshService,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var rawToken = context.Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrEmpty(rawToken))
            await refreshService.RevokeAsync(rawToken, ct);

        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("user_id")?.Value;

        if (tenantId is not null)
        {
            await authEvents.LogAsync(tenantId, userId, AuthEventTypes.Logout,
                GetIpAddress(context), GetUserAgent(context), null, ct);
        }

        context.Response.Cookies.Delete(RefreshCookieName);
        return Results.Ok(new MessageResponse("Logged out"));
    }

    // ─── API Key Login ──────────────────────────────────────────────────────────

    // Visibility elevated from `private` to `internal` so the Api.Tests project
    // (which has InternalsVisibleTo) can invoke this handler directly for MFA
    // policy enforcement regression tests (v1.9.0 P0 security fix).
    internal static async Task<IResult> ApiKeyLogin(
        [FromBody] ApiKeyLoginRequest body,
        HttpContext context,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] IUserStore userStore,
        [FromServices] ITenantAuthConfigStore configStore,
        [FromServices] IMfaPolicyEvaluator mfaPolicyEvaluator,
        JwtTokenService jwtService,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var hashedKey = HashKey(body.ApiKey);
        var apiKey = await apiKeyStore.GetByHashAsync(hashedKey, ct);

        if (apiKey is null || apiKey.IsRevoked)
            return Results.Unauthorized();

        if (apiKey.IsExpired(DateTimeOffset.UtcNow))
            return Results.Unauthorized();

        if (apiKey.UserId is not { } linkedUserId)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(apiKey.TenantId, linkedUserId, ct);
        if (user is null)
            return Results.Unauthorized();

        // v1.9.0 P0 — Tenant MFA policy enforcement on user-bound API keys (Gap 4).
        // Machine-to-machine Management keys (ApiKeyType.Management) are exempt by
        // design: they authenticate a workload, not a human, and cannot satisfy
        // an interactive TOTP challenge. User-bound Standard keys, however, are
        // a vector for MFA bypass — an admin could issue themselves a personal
        // API key and sidestep the tenant's required_all policy. Reject any
        // user-bound key whose owner's role is covered by the MFA policy.
        if (apiKey.KeyType != ApiKeyType.Management)
        {
            var policyRequiresMfa = await mfaPolicyEvaluator.RequiresMfaAsync(user.TenantId.Value, user.Role, ct);
            if (policyRequiresMfa)
            {
                await authEvents.LogAsync(user.TenantId.Value, user.UserId.Value,
                    AuthEventTypes.LoginFailure,
                    GetIpAddress(context), GetUserAgent(context),
                    new Dictionary<string, string>
                    {
                        ["reason"] = "api_key_mfa_policy_blocks",
                        ["api_key_id"] = apiKey.KeyId.Value,
                    },
                    ct);
                return Results.Json(
                    new ErrorResponse("Interactive authentication required for MFA-enforced role. Personal API keys cannot be used to bypass MFA policy."),
                    ApiJsonContext.Default.ErrorResponse,
                    statusCode: 401);
            }
        }

        // Resolve granular permissions for API key JWT
        IReadOnlySet<string>? permissions = null;
        var resolver = context.RequestServices.GetService<PermissionResolver>();
        if (resolver is not null)
        {
            try { permissions = await resolver.ResolveAsync(user.TenantId, user.UserId, ct); }
            catch { /* permissions will be resolved at authorization time instead */ }
        }

        var (accessToken, expiresAt) = jwtService.GenerateAccessToken(user, permissions);
        return Results.Ok(new TokenResponse(accessToken, expiresAt));
    }

    // ─── Change Password ────────────────────────────────────────────────────────

    internal static async Task<IResult> ChangePassword(
        [FromBody] ChangePasswordRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        [FromServices] ITenantAuthConfigStore configStore,
        AuthEventService authEvents,
        INotificationService notifications,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        // v1.9.2 Frente D — MFA step-up for password change.
        // Users with MFA enrolled must provide a fresh TOTP code in the same request.
        // This blocks stolen-session password takeover: an attacker with the session
        // cookie but not the MFA factor cannot change the password. MFA is verified
        // before the old-password check to avoid burning a password-guess budget on
        // requests that would have been rejected by MFA anyway.
        if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(body.MfaCode))
            {
                await authEvents.LogAsync(tenantId, userId, AuthEventTypes.PasswordChangeFailure,
                    GetIpAddress(context), GetUserAgent(context),
                    new Dictionary<string, string> { ["reason"] = "mfa_step_up_required" }, ct);
                return Results.Json(
                    new MfaStepUpRequiredResponse(
                        MfaStepUpRequired: true,
                        Reason: "Re-authenticate with your MFA code to change your password."),
                    ApiJsonContext.Default.MfaStepUpRequiredResponse,
                    statusCode: 401);
            }

            if (string.IsNullOrEmpty(user.MfaSecret) || !MfaService.VerifyCode(user.MfaSecret, body.MfaCode))
            {
                await authEvents.LogAsync(tenantId, userId, AuthEventTypes.PasswordChangeFailure,
                    GetIpAddress(context), GetUserAgent(context),
                    new Dictionary<string, string> { ["reason"] = "mfa_code_invalid" }, ct);
                return Results.Unauthorized();
            }
        }

        if (string.IsNullOrEmpty(user.PasswordHash) || !PasswordService.VerifyPassword(body.OldPassword, user.PasswordHash))
            return Results.BadRequest(new ErrorResponse("Current password is incorrect"));

        var config = await configStore.GetAsync(tenantId, ct) ?? new TenantAuthConfig { TenantId = tenantId };
        var validation = PasswordService.ValidatePolicy(body.NewPassword, config);
        if (!validation.IsValid)
            return Results.BadRequest(new ErrorDetailResponse("Password does not meet policy", validation.Errors));

        user.PasswordHash = PasswordService.HashPassword(body.NewPassword);
        user.PasswordChangedAt = DateTimeOffset.UtcNow;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.PasswordChange,
            GetIpAddress(context), GetUserAgent(context), null, ct);

        await notifications.CreateAsync(
            tenantId,
            "security.password_changed",
            "Password changed",
            $"User {user.Email} changed their password.",
            "/admin/security",
            ct);

        return Results.Ok(new MessageResponse("Password changed"));
    }

    // ─── Forgot Password ────────────────────────────────────────────────────────

    private static async Task<IResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        [FromServices] IPasswordResetCache resetCache,
        CancellationToken ct)
    {
        var forgotTenantId = body.TenantId;
        if (string.IsNullOrWhiteSpace(forgotTenantId) && context.Items.TryGetValue("TenantId", out var ctxForgotTenant))
            forgotTenantId = ctxForgotTenant?.ToString();

        // Always return 200 to prevent email enumeration
        if (!string.IsNullOrWhiteSpace(forgotTenantId) && !string.IsNullOrWhiteSpace(body.Email))
        {
            var user = await userStore.GetByEmailAsync(new TenantId(forgotTenantId!), body.Email, ct);
            if (user is not null)
            {
                var resetToken = GenerateToken();
                await resetCache.StoreAsync(resetToken, new PasswordResetEntry
                {
                    UserId = user.UserId.Value,
                    TenantId = forgotTenantId!,
                    ExpiresAt = DateTimeOffset.UtcNow.Add(PasswordResetTtl),
                }, ct);

                // Send password reset email
                try
                {
                    var brandingStore = context.RequestServices.GetService<ITenantBrandingStore>();
                    var emailTemplateService = context.RequestServices.GetService<IEmailTemplateService>();
                    var emailSvc = context.RequestServices.GetService<IEmailService>();
                    var smtpOpts = context.RequestServices.GetService<IOptions<SmtpOptions>>();

                    if (emailTemplateService is not null && emailSvc is not null && smtpOpts is not null)
                    {
                        var tenantBranding = brandingStore is not null
                            ? await brandingStore.GetAsync(forgotTenantId!, ct) : null;
                        var tenantSvc = context.RequestServices.GetRequiredService<ITenantStore>();
                        var tenant = await tenantSvc.GetAsync(forgotTenantId!, ct);

                        var brandingContext = new BrandingContext(
                            CompanyName: tenantBranding?.DisplayName ?? tenant?.Name ?? "Platform",
                            LogoUrl: tenantBranding?.LogoUrl,
                            PrimaryColor: tenantBranding?.PrimaryColor ?? "#1E40AF",
                            SecondaryColor: tenantBranding?.SecondaryColor ?? "#64748B",
                            AccentColor: tenantBranding?.AccentColor ?? "#0D9488",
                            SupportEmail: tenantBranding?.SupportEmail,
                            SupportUrl: tenantBranding?.SupportUrl,
                            FromName: tenantBranding?.EmailFromName ?? smtpOpts.Value.FromName,
                            FromAddress: tenantBranding?.EmailFromAddress ?? smtpOpts.Value.FromAddress);

                        var resetLink = $"{context.Request.Scheme}://{context.Request.Host}/reset-password?token={resetToken}";
                        var variables = new Dictionary<string, string>
                        {
                            ["UserEmail"] = body.Email,
                            ["ResetLink"] = resetLink,
                            ["ExpiresIn"] = "1 hour",
                        };

                        var html = emailTemplateService.Render("password-reset", brandingContext, variables);
                        var message = new EmailMessage
                        {
                            Recipients = [new EmailRecipient(body.Email, user.DisplayName)],
                            Subject = "Password Reset Request",
                            HtmlBody = html,
                            TextBody = $"Reset your password: {resetLink}\nThis link expires in 1 hour.",
                            FromName = brandingContext.FromName,
                            FromAddress = brandingContext.FromAddress,
                        };
                        await emailSvc.SendAsync(message, ct);
                    }
                }
                catch
                {
                    // Silently fail — don't reveal email existence via error
                }
            }
        }

        return Results.Ok(new MessageResponse("If the email exists, a reset link has been sent"));
    }

    // ─── Reset Password ─────────────────────────────────────────────────────────

    // Visibility elevated from `private` to `internal` so the Api.Tests project
    // (which has InternalsVisibleTo) can invoke this handler directly for MFA
    // policy enforcement regression tests (v1.9.0 P0 security fix).
    internal static async Task<IResult> ResetPassword(
        [FromBody] ResetPasswordRequest body,
        [FromServices] IUserStore userStore,
        [FromServices] ITenantAuthConfigStore configStore,
        AuthEventService authEvents,
        [FromServices] IPasswordResetCache resetCache,
        CancellationToken ct)
    {
        var entry = await resetCache.TakeAsync(body.Token, ct);
        if (entry is null)
            return Results.BadRequest(new ErrorResponse("Invalid or expired reset token"));

        var user = await userStore.GetByIdAsync(new TenantId(entry.TenantId), EntityId.From(entry.UserId), ct);
        if (user is null)
            return Results.BadRequest(new ErrorResponse("Invalid reset token"));

        var config = await configStore.GetAsync(entry.TenantId, ct) ?? new TenantAuthConfig { TenantId = entry.TenantId };
        var validation = PasswordService.ValidatePolicy(body.NewPassword, config);
        if (!validation.IsValid)
            return Results.BadRequest(new ErrorDetailResponse("Password does not meet policy", validation.Errors));

        user.PasswordHash = PasswordService.HashPassword(body.NewPassword);
        user.PasswordChangedAt = DateTimeOffset.UtcNow;
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(entry.TenantId, entry.UserId, AuthEventTypes.PasswordReset,
            null, null, null, ct);

        // v1.9.0 P0 — MFA policy enforcement after password reset (Gap 3).
        // An attacker with email access could previously reset the password and
        // log in with NO MFA verification, bypassing the tenant's policy entirely.
        // Signal the caller to complete the appropriate MFA step BEFORE any
        // session token is issued. This endpoint never returns an access token;
        // the caller must re-enter the standard login → MFA verify flow. The two
        // signals differentiate UX:
        //   • enrolled user   → action=verify (existing MFA challenge UX)
        //   • not-enrolled    → action=enroll (enrollment wizard UX)
        // If the policy does NOT require MFA for this user's role, the original
        // "password reset successful" response is preserved.
        var policyRequiresMfa = config.IsMfaRequiredForRole(user.Role.ToString());
        if (policyRequiresMfa)
        {
            if (user.MfaEnabled)
            {
                return Results.Ok(new PasswordResetMfaRequiredResponse(
                    MfaRequired: true,
                    Action: "verify",
                    Message: "Password reset successful. MFA verification is required to complete sign-in."));
            }

            return Results.Ok(new PasswordResetMfaRequiredResponse(
                MfaRequired: true,
                Action: "enroll",
                Message: "Password reset successful. MFA enrollment is required before sign-in can proceed."));
        }

        return Results.Ok(new MessageResponse("Password reset successful"));
    }

    // ─── MFA Setup ──────────────────────────────────────────────────────────────

    private static async Task<IResult> MfaSetup(
        HttpContext context,
        [FromServices] IUserStore userStore,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        var (secret, qrUri) = MfaService.GenerateSetup(user.Email);
        var recoveryCodes = MfaService.GenerateRecoveryCodes();

        // Store secret temporarily — will be confirmed by /mfa/confirm
        user.MfaSecret = secret;
        user.MfaRecoveryCodes = MfaService.HashRecoveryCodes(recoveryCodes).ToList();
        await userStore.SaveAsync(user, ct);

        return Results.Ok(new MfaSetupResponse(secret, qrUri, recoveryCodes));
    }

    // ─── MFA Confirm ────────────────────────────────────────────────────────────

    internal static async Task<IResult> MfaConfirm(
        [FromBody] MfaConfirmRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        AuthEventService authEvents,
        INotificationService notifications,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null || string.IsNullOrEmpty(user.MfaSecret))
            return Results.BadRequest(new ErrorResponse("MFA setup not initiated"));

        if (!MfaService.VerifyCode(user.MfaSecret, body.Code))
            return Results.BadRequest(new ErrorResponse("Invalid verification code"));

        user.MfaEnabled = true;
        user.MfaConfirmedAt = DateTimeOffset.UtcNow;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.MfaEnroll,
            GetIpAddress(context), GetUserAgent(context), null, ct);

        await notifications.CreateAsync(
            tenantId,
            "security.mfa_enabled",
            "Two-factor authentication enabled",
            $"User {user.Email} enabled MFA on their account.",
            "/admin/security",
            ct);

        return Results.Ok(new MessageResponse("MFA enabled"));
    }

    // ─── MFA Disable ────────────────────────────────────────────────────────────

    // Visibility elevated from `private` to `internal` so the Api.Tests project
    // (which has InternalsVisibleTo) can invoke this handler directly in unit tests.
    internal static async Task<IResult> MfaDisable(
        [FromBody] MfaDisableRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        [FromServices] ITenantAuthConfigStore tenantAuthConfigStore,
        AuthEventService authEvents,
        INotificationService notifications,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        // Sub C T0.5: enforce tenant MFA policy. A user whose role is covered by the
        // tenant's MfaPolicy ("required_all" or "required_for_roles" containing their
        // role) MUST NOT be allowed to disable MFA via the self-service endpoint.
        var authConfig = await tenantAuthConfigStore.GetAsync(tenantId, ct);
        if (authConfig is not null && authConfig.IsMfaRequiredForRole(user.Role.ToString()))
        {
            return Results.Json(
                new ErrorResponse("MFA is required by your organization and cannot be disabled."),
                ApiJsonContext.Default.ErrorResponse,
                statusCode: 403);
        }

        if (string.IsNullOrEmpty(user.PasswordHash) || !PasswordService.VerifyPassword(body.Password, user.PasswordHash))
            return Results.BadRequest(new ErrorResponse("Invalid password"));

        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaRecoveryCodes = null;
        user.MfaConfirmedAt = null;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.MfaDisable,
            GetIpAddress(context), GetUserAgent(context), null, ct);

        await notifications.CreateAsync(
            tenantId,
            "security.mfa_disabled",
            "Two-factor authentication disabled",
            $"User {user.Email} disabled MFA on their account.",
            "/admin/security",
            ct);

        return Results.Ok(new MessageResponse("MFA disabled"));
    }

    // ─── Recovery Codes Regenerate (Sub C T2.2a) ───────────────────────────────

    internal static async Task<IResult> RegenerateRecoveryCodes(
        [FromBody] RegenerateRecoveryCodesRequest request,
        HttpContext context,
        [FromServices] IUserStore userStore,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        if (!user.MfaEnabled)
            return Results.BadRequest(new ErrorResponse("MFA is not enabled for this user."));

        if (string.IsNullOrEmpty(user.PasswordHash) || !PasswordService.VerifyPassword(request.Password, user.PasswordHash))
            return Results.BadRequest(new ErrorResponse("Invalid password"));

        // Generate fresh plaintext codes to return to the caller, store hashed copies.
        var newCodes = MfaService.GenerateRecoveryCodes();
        user.MfaRecoveryCodes = MfaService.HashRecoveryCodes(newCodes).ToList();
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.RecoveryCodesRegenerated,
            GetIpAddress(context), GetUserAgent(context), null, ct);

        return Results.Ok(new RecoveryCodesResponse(newCodes.ToArray()));
    }

    // ─── Password Policy (Sub C T2.3a) ─────────────────────────────────────────

    // Returns the tenant's password policy as a sanitized DTO — only the 4 password
    // fields, never OIDC secrets, lockout, session, or MFA policy. Used by the
    // frontend to render a live validation checklist without hardcoding the rules.
    internal static async Task<IResult> GetPasswordPolicy(
        HttpContext context,
        [FromServices] ITenantAuthConfigStore tenantAuthConfigStore,
        CancellationToken ct)
    {
        var (tenantId, _) = GetAuthClaims(context);
        if (tenantId is null)
            return Results.Unauthorized();

        var config = await tenantAuthConfigStore.GetAsync(tenantId, ct);

        // Fallback to TenantAuthConfig's platform defaults when no tenant-specific config exists.
        var dto = new PasswordPolicyDto(
            MinLength: config?.PasswordMinLength ?? 12,
            RequireUppercase: config?.PasswordRequireUppercase ?? true,
            RequireNumber: config?.PasswordRequireNumber ?? true,
            RequireSpecial: config?.PasswordRequireSpecial ?? false);

        return Results.Ok(dto);
    }

    // ─── Sessions Management (Sub C T2.1a) ─────────────────────────────────────

    internal static async Task<IResult> GetOwnSessions(
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
            .Select(t => new UserSessionDto(
                t.TokenId,
                t.UserAgent,
                t.IpAddress,
                t.CreatedAt,
                t.ExpiresAt,
                IsCurrentSession: t.TokenId == currentTokenId))
            .ToArray();

        return Results.Ok(dtos);
    }

    internal static async Task<IResult> RevokeOwnSession(
        string tokenId,
        HttpContext context,
        [FromServices] IRefreshTokenStore refreshTokenStore,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var tokens = await refreshTokenStore.GetActiveByUserAsync(tenantId, userId, ct);
        var target = tokens.FirstOrDefault(t => t.TokenId == tokenId);
        if (target is null)
            return Results.NotFound();

        await refreshTokenStore.RevokeAsync(tokenId, DateTimeOffset.UtcNow, null, ct);
        return Results.Ok();
    }

    internal static async Task<IResult> RevokeOtherSessions(
        HttpContext context,
        [FromServices] IRefreshTokenStore refreshTokenStore,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var currentTokenId = await TryGetCurrentTokenIdAsync(context, refreshTokenStore, ct);
        var tokens = await refreshTokenStore.GetActiveByUserAsync(tenantId, userId, ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var token in tokens)
        {
            if (token.TokenId == currentTokenId)
                continue;
            await refreshTokenStore.RevokeAsync(token.TokenId, now, null, ct);
        }

        return Results.Ok();
    }

    private static async Task<string?> TryGetCurrentTokenIdAsync(
        HttpContext context,
        IRefreshTokenStore refreshTokenStore,
        CancellationToken ct)
    {
        if (!context.Request.Cookies.TryGetValue(RefreshCookieName, out var refreshToken) ||
            string.IsNullOrEmpty(refreshToken))
            return null;

        var hash = RefreshTokenService.HashToken(refreshToken);
        var stored = await refreshTokenStore.GetByHashAsync(hash, ct);
        return stored?.TokenId;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<IResult> IssueTokensAsync(
        User user,
        HttpContext context,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
        AccountLockoutService lockoutService,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        // AHH Phase 2 — these two writes used to be synchronous round-trips
        // to Postgres. They now route through AuthWriteQueue so the request
        // returns ~10–20 ms sooner. Failure-path audit logs (above) stay
        // synchronous per ADR-0011.
        await lockoutService.ResetAttemptsAsync(user, ct);
        await lockoutService.EnqueueLastLoginAtUpdateAsync(user, DateTimeOffset.UtcNow, ct);

        // Resolve granular permissions for JWT (best-effort, falls back to no permissions)
        IReadOnlySet<string>? permissions = null;
        var resolver = context.RequestServices.GetService<PermissionResolver>();
        if (resolver is not null)
        {
            try { permissions = await resolver.ResolveAsync(user.TenantId, user.UserId, ct); }
            catch { /* permissions will be resolved at authorization time instead */ }
        }

        // Fallback: if RBAC store returned empty and user has a privileged role,
        // grant role-based default permissions so the frontend can render correctly.
        var effectivePermissions = permissions?.ToArray() ?? [];
        if (effectivePermissions.Length == 0)
        {
            effectivePermissions = user.Role switch
            {
                UserRole.Admin => RoleDefaultPermissions.Admin,
                UserRole.Supervisor => RoleDefaultPermissions.Supervisor,
                UserRole.Agent => RoleDefaultPermissions.Agent,
                _ => [],
            };
        }

        var (accessToken, expiresAt) = jwtService.GenerateAccessToken(user, permissions);

        var ip = GetIpAddress(context);
        var ua = GetUserAgent(context);

        var (rawRefreshToken, _) = await refreshService.GenerateAsync(
            user.UserId.Value, user.TenantId.Value, ip, ua, ct);

        SetRefreshCookie(context, rawRefreshToken);

        // AHH Phase 2 — success-path AuthEvent is deferred. Failure-path
        // events still go through the synchronous LogAsync above.
        authEvents.EnqueueLogSuccess(user.TenantId.Value, user.UserId.Value,
            AuthEventTypes.LoginSuccess, ip, ua);

        // Resolve features for the response
        var featureRegistry = context.RequestServices.GetService<IFeatureRegistry>();
        var features = new Dictionary<string, bool>(featureRegistry?.GetFeatures() ?? new Dictionary<string, bool>());

        return Results.Ok(new TokenResponse(
            accessToken,
            expiresAt,
            new TokenUserDto(user.UserId.Value, user.Email, user.DisplayName, user.Role.ToString().ToLowerInvariant()),
            user.TenantId.Value,
            effectivePermissions,
            features));
    }

    private static void SetRefreshCookie(HttpContext context, string rawToken)
    {
        context.Response.Cookies.Append(RefreshCookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            MaxAge = TimeSpan.FromDays(7),
        });
    }

    private static (string? TenantId, string? UserId) GetAuthClaims(HttpContext context)
    {
        // Support both JWT claims (tid/sub) and API key claims (tenant_id/user_id).
        // Order matters: user_id wins for API keys (NameIdentifier there holds the API key id).
        // "sub" is checked before ClaimTypes.NameIdentifier because JWTs with
        // MapInboundClaims=false keep the raw "sub" claim and don't remap it.
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        var userId = context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return (tenantId, userId);
    }

    private static string? GetIpAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    private static string? GetUserAgent(HttpContext context) =>
        context.Request.Headers.UserAgent.FirstOrDefault();

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // Called from OidcEndpoints.CompleteOidcLoginAsync when the OIDC user has MFA
    // enrolled — creates a pending challenge entry and returns the challenge token.
    // Both classes live in the same assembly (Verbara.Platform.Api), so internal
    // access is sufficient.
    internal static async Task<string> GenerateMfaChallengeTokenAndStoreAsync(
        string userId, string tenantId, IMfaPendingCache mfaCache, CancellationToken ct)
    {
        var challengeToken = GenerateToken();
        await mfaCache.StoreAsync(challengeToken, new MfaPendingEntry
        {
            UserId = userId,
            TenantId = tenantId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(MfaPendingTtl),
        }, ct);
        return challengeToken;
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record LoginRequest(string? TenantId, string Email, string Password);
internal sealed record ApiKeyLoginRequest(string ApiKey);
internal sealed record ChangePasswordRequest(string OldPassword, string NewPassword, string? MfaCode = null);

/// <summary>
/// Returned as HTTP 401 when the user has MFA enrolled and did not include a TOTP code
/// in the change-password request. The client must re-submit with MfaCode populated.
/// No transient challenge token is issued — the user's TOTP device is the source of truth.
/// </summary>
internal sealed record MfaStepUpRequiredResponse(bool MfaStepUpRequired, string Reason);
internal sealed record ForgotPasswordRequest(string TenantId, string Email);
internal sealed record ResetPasswordRequest(string Token, string NewPassword);
internal sealed record MfaVerifyRequest(string MfaToken, string? Code, string? RecoveryCode);
internal sealed record MfaConfirmRequest(string Code);
internal sealed record MfaDisableRequest(string Password);
internal sealed record MfaSetupResponse(string Secret, string QrUri, IReadOnlyList<string> RecoveryCodes);
internal sealed record RegenerateRecoveryCodesRequest(string Password);
internal sealed record RecoveryCodesResponse(string[] RecoveryCodes);
internal sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    TokenUserDto? User = null,
    string? TenantId = null,
    string[]? Permissions = null,
    Dictionary<string, bool>? Features = null);

internal sealed record TokenUserDto(string Id, string Email, string DisplayName, string Role);

/// <summary>
/// Default permissions per role when RBAC store has no data (InMemory / no Postgres).
/// Mirrors the role templates defined in RoleTemplateSeeder.
/// </summary>
internal static class RoleDefaultPermissions
{
    public static readonly string[] Admin =
    [
        "contacts:conversation:handle", "contacts:conversation:transfer", "contacts:conversation:monitor",
        "contacts:conversation:barge", "contacts:conversation:whisper", "contacts:contact:view",
        "contacts:contact:edit", "contacts:contact:create",
        "queues:queue:view", "queues:queue:create", "queues:queue:edit", "queues:queue:delete",
        "queues:member:assign",
        "users:user:view", "users:user:create", "users:user:edit", "users:user:deactivate",
        "users:role:assign",
        "campaigns:campaign:view", "campaigns:campaign:create", "campaigns:campaign:edit",
        "campaigns:campaign:delete", "campaigns:campaign:execute",
        "campaigns:dnc:manage", "campaigns:callerid:manage", "campaigns:calendar:manage",
        "campaigns:route:manage", "campaigns:trunk:manage", "campaigns:dialer:configure",
        "reporting:realtime:view", "reporting:historical:view", "reporting:historical:export",
        "reporting:dashboard:edit",
        "quality:evaluation:view", "quality:evaluation:score", "quality:scorecard:manage",
        "recording:recording:play", "recording:recording:delete", "recording:recording:export",
        "routing:skill:view", "routing:skill:manage", "routing:flow:view", "routing:flow:edit",
        "analytics:cdr:view", "analytics:cdr:export", "analytics:interval:view", "analytics:alert:manage",
        "system:tenant:configure", "system:integration:manage", "system:audit:view",
        "system:auth:configure", "system:cluster:manage",
        "agentassist:session:view", "agentassist:config:manage",
        "callanalytics:analysis:view", "callanalytics:config:manage",
    ];

    public static readonly string[] Supervisor =
    [
        "contacts:conversation:handle", "contacts:conversation:transfer", "contacts:conversation:monitor",
        "contacts:conversation:barge", "contacts:conversation:whisper", "contacts:contact:view",
        "queues:queue:view", "queues:queue:edit", "queues:member:assign",
        "users:user:view",
        "reporting:realtime:view", "reporting:historical:view",
        "quality:evaluation:view", "quality:evaluation:score",
        "recording:recording:play",
        "analytics:cdr:view", "analytics:interval:view",
    ];

    public static readonly string[] Agent =
    [
        "contacts:conversation:handle", "contacts:conversation:transfer", "contacts:contact:view",
        "reporting:realtime:view",
    ];
}

internal sealed record MfaChallengeResponse(bool RequiresMfa, string MfaToken);

// v1.9.0 P0 — Login / Refresh / ApiKeyLogin signal when tenant policy requires
// MFA and the user has not yet enrolled. Returned with HTTP 401. Distinct from
// MfaChallengeResponse (which signals "complete the TOTP challenge") because
// the user has no TOTP secret yet — the frontend must drive an enrollment
// wizard before allowing sign-in.
internal sealed record MfaEnrollmentRequiredResponse(bool MfaEnrollmentRequired, string Reason);

// v1.9.0 P0 — ResetPassword signal that the password change succeeded but the
// tenant's MFA policy requires an additional step before any session token is
// issued. Action = "verify" (user is already enrolled, run MFA challenge) or
// "enroll" (user must complete MFA enrollment). Returned with HTTP 200 because
// the password change itself succeeded — the signal gates the follow-on sign-in.
internal sealed record PasswordResetMfaRequiredResponse(bool MfaRequired, string Action, string Message);

internal sealed record AuthEventDetail(string? Email = null, string? Reason = null);

internal sealed record UserSessionDto(
    string TokenId,
    string? UserAgent,
    string? IpAddress,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrentSession);

// Sub C T2.3a: sanitized password policy DTO. MUST NOT include OIDC secrets,
// lockout config, session timeouts, MFA policy, or any other TenantAuthConfig
// fields — only the 4 password-related flags. Note: TenantAuthConfig has no
// PasswordRequireLowercase field, so neither does this DTO.
internal sealed record PasswordPolicyDto(
    int MinLength,
    bool RequireUppercase,
    bool RequireNumber,
    bool RequireSpecial);

