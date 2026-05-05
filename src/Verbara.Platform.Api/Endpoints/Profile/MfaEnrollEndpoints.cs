using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints.Profile;

/// <summary>
/// 3-step MFA enrollment state machine consumed by the end-user wizard at
/// <c>/profile/security/mfa/enroll</c> (R5.2 PA.2 / S3.4 Frente E).
/// </summary>
/// <remarks>
/// <para>
/// Step 1 — <c>POST /profile/security/mfa/enroll/init</c> generates a fresh TOTP
/// secret + QR URI but does NOT yet persist anything. The caller carries the
/// secret across to step 2.
/// </para>
/// <para>
/// Step 2 — <c>POST /profile/security/mfa/enroll/verify</c> takes
/// <c>{ secret, totpCode }</c> and persists the secret + freshly-minted recovery
/// codes only when the TOTP validates against the supplied secret. Returns the
/// plaintext recovery codes ONCE.
/// </para>
/// <para>
/// Step 3 — <c>POST /profile/security/mfa/enroll/complete</c> takes
/// <c>{ acknowledged: true }</c> and finalises the enrollment by setting
/// <c>MfaEnabled = true</c> + <c>MfaConfirmedAt</c>. The split between verify
/// and complete makes the flow idempotent and gives the wizard the chance to
/// abort if the user fails to save the recovery codes.
/// </para>
/// <para>
/// The legacy <c>/auth/mfa/{setup,confirm}</c> surface is preserved unchanged
/// for the existing in-place security page; this profile-scoped surface
/// complements it for the dedicated wizard route.
/// </para>
/// </remarks>
internal static class MfaEnrollEndpoints
{
    public static void MapMfaEnrollEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/profile/security/mfa/enroll")
            .RequireAuthorization();

        group.MapPost("/init", Init);
        group.MapPost("/verify", Verify);
        group.MapPost("/complete", Complete);
    }

    /// <summary>
    /// Step 1 — generate a fresh TOTP secret + QR URI. Nothing is committed
    /// to the user record yet; the caller must replay the secret in step 2.
    /// </summary>
    internal static async Task<IResult> Init(
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

        if (user.MfaEnabled)
            return Results.BadRequest(new ErrorResponse("MFA already enrolled. Disable first to re-enroll."));

        var (secret, qrUri) = MfaService.GenerateSetup(user.Email);

        return Results.Ok(new MfaEnrollInitResponse(
            Secret: secret,
            QrUri: qrUri,
            ManualCode: secret));
    }

    /// <summary>
    /// Step 2 — validate the TOTP against the supplied secret; on success,
    /// persist the secret + freshly-minted recovery codes and return the
    /// plaintext codes one time.
    /// </summary>
    internal static async Task<IResult> Verify(
        [FromBody] MfaEnrollVerifyRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        [FromServices] IRecoveryCodeService recoveryCodes,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(body.Secret) || string.IsNullOrWhiteSpace(body.TotpCode))
            return Results.BadRequest(new ErrorResponse("secret and totpCode are required"));

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        if (user.MfaEnabled)
            return Results.BadRequest(new ErrorResponse("MFA already enrolled."));

        if (!MfaService.VerifyCode(body.Secret, body.TotpCode))
            return Results.BadRequest(new ErrorResponse("Invalid verification code"));

        // Generate plaintext codes to return ONCE; store SHA-256 + per-user
        // salt hashes. The salt is the user's stable EntityId (never reused,
        // never empty) so we don't need a schema migration to add a salt
        // column today (R5.2 PA.2).
        var codes = recoveryCodes.Generate();
        var salt = user.UserId.Value;
        var hashed = codes.Select(c => recoveryCodes.Hash(c, salt)).ToList();

        user.MfaSecret = body.Secret;
        user.MfaRecoveryCodes = hashed;
        // Don't flip MfaEnabled yet — that happens in /complete after the
        // user acknowledges saving the codes.
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.MfaEnroll,
            GetIpAddress(context), GetUserAgent(context), null, ct);

        return Results.Ok(new RecoveryCodesPayload(codes.ToArray()));
    }

    /// <summary>
    /// Step 3 — finalise enrollment after the user acknowledges saving the
    /// recovery codes. Idempotent: replays return 200 with no side effect.
    /// </summary>
    internal static async Task<IResult> Complete(
        [FromBody] MfaEnrollCompleteRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        AuthEventService authEvents,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        if (!body.Acknowledged)
            return Results.BadRequest(new ErrorResponse("acknowledged must be true"));

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        if (string.IsNullOrEmpty(user.MfaSecret))
            return Results.BadRequest(new ErrorResponse("Enrollment not initialised. Call /init then /verify first."));

        // Idempotent: if already enabled, just return the same status.
        if (!user.MfaEnabled)
        {
            user.MfaEnabled = true;
            user.MfaConfirmedAt = DateTimeOffset.UtcNow;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.SaveAsync(user, ct);

            await authEvents.LogAsync(tenantId, userId, AuthEventTypes.MfaEnroll,
                GetIpAddress(context), GetUserAgent(context), null, ct);
        }

        return Results.NoContent();
    }

    // ─── Helpers (mirrored from AuthEndpoints — kept private to this surface) ───

    private static (string? TenantId, string? UserId) GetAuthClaims(HttpContext context)
    {
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
}

// ─── DTOs ──────────────────────────────────────────────────────────────────

internal sealed record MfaEnrollInitResponse(string Secret, string QrUri, string ManualCode);

internal sealed record MfaEnrollVerifyRequest(string Secret, string TotpCode);

internal sealed record MfaEnrollCompleteRequest(bool Acknowledged);

internal sealed record RecoveryCodesPayload(string[] RecoveryCodes);
