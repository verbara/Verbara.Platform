using System.Security.Claims;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints.Profile;

/// <summary>
/// Profile-scoped recovery-code regeneration backing the wizard at
/// <c>/profile/security/recovery-codes/regenerate</c> (R5.2 PA.2).
/// </summary>
/// <remarks>
/// <para>
/// The legacy <c>POST /auth/mfa/recovery-codes/regenerate</c> requires a
/// password challenge; this profile-scoped variant requires a fresh TOTP step-up
/// instead — matching the modern UX where the user is in a route gated by an
/// authenticated session and only needs to prove possession of the second factor.
/// The pattern mirrors <c>POST /auth/change-password</c> step-up (v1.9.2).
/// </para>
/// <para>
/// Generated codes use the new SHA-256 + per-user salt scheme via
/// <see cref="IRecoveryCodeService"/> (8-char Base32) and are returned ONCE.
/// Storage retains hashes only.
/// </para>
/// </remarks>
internal static class ProfileRecoveryCodesEndpoints
{
    public static void MapProfileRecoveryCodesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/profile/security/recovery-codes")
            .RequireAuthorization();

        group.MapPost("/regenerate", Regenerate);
    }

    /// <summary>
    /// Regenerates the user's recovery codes after a fresh TOTP step-up.
    /// Returns the plaintext codes ONCE; stores SHA-256 + per-user salt hashes.
    /// </summary>
    internal static async Task<IResult> Regenerate(
        [FromBody] ProfileRegenerateRecoveryCodesRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        [FromServices] IRecoveryCodeService recoveryCodes,
        AuthEventService authEvents,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var (tenantId, userId) = GetAuthClaims(context);
        if (tenantId is null || userId is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Results.Unauthorized();

        if (!user.MfaEnabled || string.IsNullOrEmpty(user.MfaSecret))
            return Results.BadRequest(new ErrorResponse("MFA is not enabled for this user."));

        // Step-up: require a fresh TOTP from the user's authenticator. We do NOT
        // accept a recovery code here (would be circular: a recovery code can
        // already replace TOTP, and we're regenerating those very codes).
        if (string.IsNullOrWhiteSpace(body.TotpCode))
        {
            await authEvents.LogAsync(tenantId, userId, AuthEventTypes.RecoveryCodesRegenerated,
                GetIpAddress(context), GetUserAgent(context),
                new Dictionary<string, string> { ["reason"] = "mfa_step_up_required" }, ct);
            return Results.Json(
                new MfaStepUpRequiredResponse(
                    MfaStepUpRequired: true,
                    Reason: "Provide a fresh MFA code to regenerate recovery codes."),
                ApiJsonContext.Default.MfaStepUpRequiredResponse,
                statusCode: 401);
        }

        if (!MfaService.VerifyCode(user.MfaSecret, body.TotpCode))
        {
            await authEvents.LogAsync(tenantId, userId, AuthEventTypes.RecoveryCodesRegenerated,
                GetIpAddress(context), GetUserAgent(context),
                new Dictionary<string, string> { ["reason"] = "mfa_code_invalid" }, ct);
            return Results.Unauthorized();
        }

        var codes = recoveryCodes.Generate();
        var salt = user.UserId.Value;
        user.MfaRecoveryCodes = codes.Select(c => recoveryCodes.Hash(c, salt)).ToList();
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await userStore.SaveAsync(user, ct);

        await authEvents.LogAsync(tenantId, userId, AuthEventTypes.RecoveryCodesRegenerated,
            GetIpAddress(context), GetUserAgent(context), null, ct);

        await audit.RecordAsync(
            new TenantId(tenantId),
            category: "auth",
            action: "mfa.recovery_codes.regenerated",
            severity: "info",
            actorId: userId,
            actorType: "user",
            targetId: userId,
            targetType: "user",
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["actor_tenant_id"] = tenantId,
                ["ip"] = GetIpAddress(context) ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);

        return Results.Ok(new RecoveryCodesPayload(codes.ToArray()));
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

    private static string? GetIpAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    private static string? GetUserAgent(HttpContext context) =>
        context.Request.Headers.UserAgent.FirstOrDefault();
}

internal sealed record ProfileRegenerateRecoveryCodesRequest(string TotpCode);
