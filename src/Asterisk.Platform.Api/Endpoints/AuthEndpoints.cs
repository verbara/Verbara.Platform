using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Endpoints;

internal static class AuthEndpoints
{
    private const string RefreshCookieName = "refresh_token";
    private static readonly TimeSpan MfaPendingTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PasswordResetTtl = TimeSpan.FromHours(1);

    // In-memory caches for MFA challenge tokens and password reset tokens.
    // Production systems should use a distributed cache, but for now
    // these match the spec's ConcurrentDictionary approach.
    internal static readonly ConcurrentDictionary<string, MfaPendingEntry> MfaPendingCache = new();
    internal static readonly ConcurrentDictionary<string, PasswordResetEntry> PasswordResetCache = new();

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

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
    }

    // ─── Login ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> Login(
        LoginRequest body,
        HttpContext context,
        IUserStore userStore,
        PasswordService passwordService,
        AccountLockoutService lockoutService,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
        AuthEventService authEvents,
        ITenantAuthConfigStore configStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.TenantId))
            return Results.BadRequest(new { error = "Tenant ID is required" });

        var tenantId = new TenantId(body.TenantId);
        var ip = GetIpAddress(context);
        var ua = GetUserAgent(context);

        var user = await userStore.GetByEmailAsync(tenantId, body.Email, ct);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            await authEvents.LogAsync(body.TenantId, null, AuthEventTypes.LoginFailure, ip, ua,
                new { email = body.Email, reason = "invalid_credentials" }, ct);
            return Results.Unauthorized();
        }

        if (user.IsLockedOut(DateTimeOffset.UtcNow))
            return Results.Json(new { error = "Account is locked" }, statusCode: 423);

        if (!passwordService.VerifyPassword(body.Password, user.PasswordHash))
        {
            await lockoutService.RecordFailedAttemptAsync(user, ip, ua, ct);
            await authEvents.LogAsync(body.TenantId, user.UserId.Value, AuthEventTypes.LoginFailure, ip, ua,
                new { reason = "invalid_password" }, ct);
            return Results.Unauthorized();
        }

        // Check MFA
        if (user.MfaEnabled)
        {
            var challengeToken = GenerateToken();
            MfaPendingCache[challengeToken] = new MfaPendingEntry
            {
                UserId = user.UserId.Value,
                TenantId = body.TenantId,
                ExpiresAt = DateTimeOffset.UtcNow.Add(MfaPendingTtl),
            };

            return Results.Ok(new { mfaRequired = true, challengeToken });
        }

        // Issue tokens
        return await IssueTokensAsync(user, context, jwtService, refreshService, lockoutService, authEvents, ct);
    }

    // ─── MFA Verify ──────────────────────────────────────────────────────────────

    private static async Task<IResult> MfaVerify(
        MfaVerifyRequest body,
        HttpContext context,
        IUserStore userStore,
        MfaService mfaService,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
        AccountLockoutService lockoutService,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        if (!MfaPendingCache.TryRemove(body.ChallengeToken, out var pending))
            return Results.BadRequest(new { error = "Invalid or expired challenge token" });

        if (pending.ExpiresAt < DateTimeOffset.UtcNow)
            return Results.BadRequest(new { error = "Challenge token expired" });

        var tenantId = new TenantId(pending.TenantId);
        var user = await userStore.GetByIdAsync(tenantId, EntityId.From(pending.UserId), ct);
        if (user is null)
            return Results.Unauthorized();

        // Try TOTP code first, then recovery code
        var verified = false;
        if (!string.IsNullOrEmpty(body.Code) && !string.IsNullOrEmpty(user.MfaSecret))
        {
            verified = mfaService.VerifyCode(user.MfaSecret, body.Code);
        }

        if (!verified && !string.IsNullOrEmpty(body.RecoveryCode) && user.MfaRecoveryCodes is { Count: > 0 })
        {
            var (isValid, index) = mfaService.ValidateRecoveryCode(body.RecoveryCode, user.MfaRecoveryCodes);
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

    private static async Task<IResult> Refresh(
        HttpContext context,
        IUserStore userStore,
        JwtTokenService jwtService,
        RefreshTokenService refreshService,
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

        return Results.Ok(new TokenResponse(accessToken, expiresAt));
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
        return Results.Ok(new { message = "Logged out" });
    }

    // ─── API Key Login ──────────────────────────────────────────────────────────

    private static async Task<IResult> ApiKeyLogin(
        ApiKeyLoginRequest body,
        HttpContext context,
        IApiKeyStore apiKeyStore,
        IUserStore userStore,
        JwtTokenService jwtService,
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

    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest body,
        HttpContext context,
        IUserStore userStore,
        PasswordService passwordService,
        ITenantAuthConfigStore configStore,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        if (string.IsNullOrEmpty(user.PasswordHash) || !passwordService.VerifyPassword(body.OldPassword, user.PasswordHash))
            return Results.BadRequest(new { error = "Current password is incorrect" });

        var config = await configStore.GetAsync(tenantId, ct) ?? new TenantAuthConfig { TenantId = tenantId };
        var validation = passwordService.ValidatePolicy(body.NewPassword, config);
        if (!validation.IsValid)
            return Results.BadRequest(new { error = "Password does not meet policy", details = validation.Errors });

        user.PasswordHash = passwordService.HashPassword(body.NewPassword);
        user.PasswordChangedAt = DateTimeOffset.UtcNow;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.PasswordChange,
            GetIpAddress(context), GetUserAgent(context), null, ct);

        return Results.Ok(new { message = "Password changed" });
    }

    // ─── Forgot Password ────────────────────────────────────────────────────────

    private static async Task<IResult> ForgotPassword(
        ForgotPasswordRequest body,
        IUserStore userStore,
        CancellationToken ct)
    {
        // Always return 200 to prevent email enumeration
        if (!string.IsNullOrWhiteSpace(body.TenantId) && !string.IsNullOrWhiteSpace(body.Email))
        {
            var user = await userStore.GetByEmailAsync(new TenantId(body.TenantId), body.Email, ct);
            if (user is not null)
            {
                var resetToken = GenerateToken();
                PasswordResetCache[resetToken] = new PasswordResetEntry
                {
                    UserId = user.UserId.Value,
                    TenantId = body.TenantId,
                    ExpiresAt = DateTimeOffset.UtcNow.Add(PasswordResetTtl),
                };

                // In production, send email with resetToken.
                // For now, token is stored in cache for /reset-password endpoint.
            }
        }

        return Results.Ok(new { message = "If the email exists, a reset link has been sent" });
    }

    // ─── Reset Password ─────────────────────────────────────────────────────────

    private static async Task<IResult> ResetPassword(
        ResetPasswordRequest body,
        IUserStore userStore,
        PasswordService passwordService,
        ITenantAuthConfigStore configStore,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        if (!PasswordResetCache.TryRemove(body.Token, out var entry))
            return Results.BadRequest(new { error = "Invalid or expired reset token" });

        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
            return Results.BadRequest(new { error = "Reset token expired" });

        var user = await userStore.GetByIdAsync(new TenantId(entry.TenantId), EntityId.From(entry.UserId), ct);
        if (user is null)
            return Results.BadRequest(new { error = "Invalid reset token" });

        var config = await configStore.GetAsync(entry.TenantId, ct) ?? new TenantAuthConfig { TenantId = entry.TenantId };
        var validation = passwordService.ValidatePolicy(body.NewPassword, config);
        if (!validation.IsValid)
            return Results.BadRequest(new { error = "Password does not meet policy", details = validation.Errors });

        user.PasswordHash = passwordService.HashPassword(body.NewPassword);
        user.PasswordChangedAt = DateTimeOffset.UtcNow;
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(entry.TenantId, entry.UserId, AuthEventTypes.PasswordReset,
            null, null, null, ct);

        return Results.Ok(new { message = "Password reset successful" });
    }

    // ─── MFA Setup ──────────────────────────────────────────────────────────────

    private static async Task<IResult> MfaSetup(
        HttpContext context,
        IUserStore userStore,
        MfaService mfaService,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        var (secret, qrUri) = mfaService.GenerateSetup(user.Email);
        var recoveryCodes = mfaService.GenerateRecoveryCodes();

        // Store secret temporarily — will be confirmed by /mfa/confirm
        user.MfaSecret = secret;
        user.MfaRecoveryCodes = mfaService.HashRecoveryCodes(recoveryCodes).ToList();
        await userStore.SaveAsync(user, ct);

        return Results.Ok(new MfaSetupResponse(secret, qrUri, recoveryCodes));
    }

    // ─── MFA Confirm ────────────────────────────────────────────────────────────

    private static async Task<IResult> MfaConfirm(
        MfaConfirmRequest body,
        HttpContext context,
        IUserStore userStore,
        MfaService mfaService,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null || string.IsNullOrEmpty(user.MfaSecret))
            return Results.BadRequest(new { error = "MFA setup not initiated" });

        if (!mfaService.VerifyCode(user.MfaSecret, body.Code))
            return Results.BadRequest(new { error = "Invalid verification code" });

        user.MfaEnabled = true;
        user.MfaConfirmedAt = DateTimeOffset.UtcNow;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.MfaEnroll,
            GetIpAddress(context), GetUserAgent(context), null, ct);

        return Results.Ok(new { message = "MFA enabled" });
    }

    // ─── MFA Disable ────────────────────────────────────────────────────────────

    private static async Task<IResult> MfaDisable(
        [Microsoft.AspNetCore.Mvc.FromBody] MfaDisableRequest body,
        HttpContext context,
        IUserStore userStore,
        PasswordService passwordService,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        if (string.IsNullOrEmpty(user.PasswordHash) || !passwordService.VerifyPassword(body.Password, user.PasswordHash))
            return Results.BadRequest(new { error = "Invalid password" });

        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaRecoveryCodes = null;
        user.MfaConfirmedAt = null;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.MfaDisable,
            GetIpAddress(context), GetUserAgent(context), null, ct);

        return Results.Ok(new { message = "MFA disabled" });
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
        await lockoutService.ResetAttemptsAsync(user, ct);

        user.LastLoginAt = DateTimeOffset.UtcNow;

        // Resolve granular permissions for JWT (best-effort, falls back to no permissions)
        IReadOnlySet<string>? permissions = null;
        var resolver = context.RequestServices.GetService<PermissionResolver>();
        if (resolver is not null)
        {
            try { permissions = await resolver.ResolveAsync(user.TenantId, user.UserId, ct); }
            catch { /* permissions will be resolved at authorization time instead */ }
        }

        var (accessToken, expiresAt) = jwtService.GenerateAccessToken(user, permissions);

        var ip = GetIpAddress(context);
        var ua = GetUserAgent(context);

        var (rawRefreshToken, _) = await refreshService.GenerateAsync(
            user.UserId.Value, user.TenantId.Value, ip, ua, ct);

        SetRefreshCookie(context, rawRefreshToken);

        await authEvents.LogAsync(user.TenantId.Value, user.UserId.Value,
            AuthEventTypes.LoginSuccess, ip, ua, null, ct);

        return Results.Ok(new TokenResponse(accessToken, expiresAt));
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
        // Check user_id first because API key auth sets NameIdentifier to the key ID.
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        var userId = context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return (tenantId, userId);
    }

    private static string? GetIpAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    private static string? GetUserAgent(HttpContext context) =>
        context.Request.Headers.UserAgent.FirstOrDefault();

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record LoginRequest(string TenantId, string Email, string Password);
internal sealed record ApiKeyLoginRequest(string ApiKey);
internal sealed record ChangePasswordRequest(string OldPassword, string NewPassword);
internal sealed record ForgotPasswordRequest(string TenantId, string Email);
internal sealed record ResetPasswordRequest(string Token, string NewPassword);
internal sealed record MfaVerifyRequest(string ChallengeToken, string? Code, string? RecoveryCode);
internal sealed record MfaConfirmRequest(string Code);
internal sealed record MfaDisableRequest(string Password);
internal sealed record MfaSetupResponse(string Secret, string QrUri, IReadOnlyList<string> RecoveryCodes);
internal sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

internal sealed class MfaPendingEntry
{
    public required string UserId { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class PasswordResetEntry
{
    public required string UserId { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
