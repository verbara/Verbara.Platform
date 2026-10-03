using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Auth;

/// <summary>
/// How the Api refuses an account that may not authenticate. The rule itself is
/// <see cref="User.CanAuthenticate"/>; this class only keeps the refusals uniform: one recorded
/// reason, one response body, one 403 for the sign-in endpoints.
/// </summary>
internal static class AccountStatusGate
{
    /// <summary>The body of every refusal. Names no status on purpose.</summary>
    internal const string DeniedMessage = "Account is not active.";

    /// <summary>The impersonation-token claim that records its impersonator's role when it was minted.</summary>
    internal const string ImpersonatorRoleClaim = "impersonator_role";

    /// <summary>Whether <paramref name="principal"/> was authenticated by an impersonation token.</summary>
    internal static bool IsImpersonation(ClaimsPrincipal principal) =>
        Verbara.Platform.Identity.Auth.ImpersonationTokenRevocation.IsImpersonation(principal);

    /// <summary>
    /// The per-request half of the rule, for impersonation tokens only: whether the admin an
    /// impersonation token acts for may still authenticate, and still holds the role the token was
    /// minted for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An ordinary access token outlives a suspension by at most its 15-minute lifetime, the
    /// residual accepted in place of a per-request lookup. An impersonation token lives 30 minutes
    /// and grants Admin in another tenant, so it is held to its impersonator's status on every
    /// request instead — the same per-request rule a user-bound API key meets. The lookup goes
    /// through the cached <see cref="IUserStore"/>, which a status or role change invalidates.
    /// </para>
    /// <para>
    /// The same lookup holds the token to the role its impersonator had when it was minted
    /// (<see cref="ImpersonatorRoleClaim"/>): any role change ends the impersonation. That role is
    /// Admin (<c>StartImpersonation</c> refuses any other), except for a session started with a
    /// management key, where it is the key owner's role.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> when the impersonator is not Active, no longer exists, holds another
    /// role than the token records, or the token does not name it or its role (every impersonation
    /// token Platform.Api mints names both).
    /// </returns>
    internal static async Task<bool> ImpersonatorMayAuthenticateAsync(
        ClaimsPrincipal principal, IUserStore users, CancellationToken ct)
    {
        var tenantId = principal.FindFirst("impersonator_tenant")?.Value;
        var userId = principal.FindFirst("impersonator_id")?.Value;
        var mintedRole = principal.FindFirst(ImpersonatorRoleClaim)?.Value;
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(mintedRole))
            return false;

        var impersonator = await users.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct)
            .ConfigureAwait(false);
        return impersonator is { CanAuthenticate: true }
            && string.Equals(impersonator.Role.ToString(), mintedRole, StringComparison.Ordinal);
    }

    /// <summary>The account's status as recorded in auth events, audit entries and push events (lower case).</summary>
    internal static string StatusName(UserStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>The auth-event reason recorded when the account is refused, e.g. <c>account_suspended</c>.</summary>
    internal static string DenialReason(User user) => "account_" + StatusName(user.Status);

    /// <summary>
    /// Records a refused sign-in for an account that proved its credentials but may not
    /// authenticate. Synchronous, like every failure-path auth event (ADR-0011).
    /// </summary>
    /// <param name="authEvents">Where the refusal is recorded.</param>
    /// <param name="user">The refused account.</param>
    /// <param name="flow">Which sign-in path refused it: <c>password</c>, <c>mfa</c>, <c>api_key</c>, <c>impersonation</c>.</param>
    /// <param name="context">The request, for the caller's IP and user agent.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="apiKeyId">The key that was presented, on the API-key path.</param>
    internal static Task RecordRefusedSignInAsync(
        AuthEventService authEvents,
        User user,
        string flow,
        HttpContext context,
        CancellationToken ct,
        string? apiKeyId = null)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = DenialReason(user),
            ["flow"] = flow,
        };
        if (apiKeyId is not null)
            details["api_key_id"] = apiKeyId;

        return authEvents.LogAsync(
            user.TenantId.Value,
            user.UserId.Value,
            AuthEventTypes.LoginFailure,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.FirstOrDefault(),
            details,
            ct);
    }

    /// <summary>
    /// The sign-in endpoints' answer to an account that proved its credentials but may not
    /// authenticate: 403, distinct from the 401 a wrong password gets.
    /// </summary>
    internal static IResult Forbidden() =>
        Results.Json(
            new ErrorResponse(DeniedMessage),
            ApiJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status403Forbidden);
}
