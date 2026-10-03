using System.Collections.Frozen;
using System.Security.Claims;
using System.Text.Json;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Refuses, with 403, what a request made with an impersonation token may not do: the operations
/// blocked during every impersonation (<see cref="IsBlockedDuringImpersonation"/>), and every write but
/// ending the session during a read-only one (<see cref="IsBlockedInReadOnlyMode"/>).
/// </summary>
/// <remarks>
/// <para>
/// Registered after <c>UseAuthentication()</c>, because both rules read the authenticated principal
/// (before authentication it is anonymous), and before <c>UseAuthorization()</c>, so a refusal here
/// comes before any authorization handler runs.
/// </para>
/// <para>
/// Both rules match the endpoint the request was routed to — its HTTP method and route template —
/// never the raw path. The version segment accepts aliases (<c>/api/v1.0/</c> reaches the endpoint
/// <c>/api/v1/</c> does), and an unversioned path or a trailing slash reaches it too; the template is
/// the same for all of them.
/// </para>
/// </remarks>
internal sealed class ImpersonationGuardMiddleware
{
    internal const string DuringImpersonationMessage = "Operation not allowed during impersonation";
    internal const string ReadOnlyMessage = "Operation not allowed in read-only impersonation mode";

    private const string Api = "/api/v{version:apiVersion}";

    /// <summary>Operations no impersonation session may perform, as <c>METHOD template</c>.</summary>
    internal static readonly FrozenSet<string> BlockedDuringImpersonation = new[]
    {
        // Starting another impersonation, and first-run setup.
        $"POST {Api}/management/impersonate",
        $"POST {Api}/setup",

        // The signed-in account's own password, MFA, recovery codes and sessions.
        $"POST {Api}/auth/change-password",
        $"POST {Api}/auth/mfa/setup",
        $"POST {Api}/auth/mfa/confirm",
        $"DELETE {Api}/auth/mfa",
        $"POST {Api}/auth/mfa/recovery-codes/regenerate",
        $"POST {Api}/auth/sessions/revoke-others",
        $"POST {Api}/profile/security/mfa/enroll/init",
        $"POST {Api}/profile/security/mfa/enroll/verify",
        $"POST {Api}/profile/security/mfa/enroll/complete",
        $"POST {Api}/profile/security/recovery-codes/regenerate",
        $"POST {Api}/profile/security/sessions/{{tokenId}}/revoke",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Operations no impersonation session may perform, as a method and the route-template prefix of
    /// every endpoint they cover.
    /// </summary>
    internal static readonly IReadOnlyList<(string Method, string TemplatePrefix)> BlockedPrefixesDuringImpersonation =
    [
        ("DELETE", $"{Api}/auth/sessions/"),       // the signed-in account's own sessions
        ("DELETE", $"{Api}/admin/auth/sessions/"), // another user's sessions
        ("DELETE", $"{Api}/management/tenants/"),  // a tenant, or anything under one
        ("PUT", $"{Api}/management/system/"),      // installation settings and license
    ];

    /// <summary>
    /// The only operations other than GET, HEAD and OPTIONS a read-only session may perform, as
    /// <c>METHOD template</c>.
    /// </summary>
    internal static readonly FrozenSet<string> AllowedInReadOnly = new[]
    {
        $"DELETE {Api}/management/impersonate", // ending the session
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly RequestDelegate _next;

    public ImpersonationGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true || !AccountStatusGate.IsImpersonation(user))
        {
            await _next(context);
            return;
        }

        var method = context.Request.Method;
        var template = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

        if (IsBlockedDuringImpersonation(method, template))
        {
            await RefuseAsync(context, DuringImpersonationMessage);
            return;
        }

        if (string.Equals(user.FindFirstValue("readonly"), "true", StringComparison.Ordinal)
            && IsBlockedInReadOnlyMode(method, template))
        {
            await RefuseAsync(context, ReadOnlyMessage);
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Whether <paramref name="method"/> on the endpoint whose route template is
    /// <paramref name="template"/> is refused to every impersonation session. A request that matched
    /// no endpoint (<paramref name="template"/> <see langword="null"/>) is not: it reaches no operation.
    /// </summary>
    internal static bool IsBlockedDuringImpersonation(string method, string? template)
    {
        if (template is null)
            return false;

        if (BlockedDuringImpersonation.Contains(Key(method, template)))
            return true;

        foreach (var (blockedMethod, prefix) in BlockedPrefixesDuringImpersonation)
        {
            if (string.Equals(method, blockedMethod, StringComparison.OrdinalIgnoreCase)
                && template.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="method"/> on the endpoint whose route template is
    /// <paramref name="template"/> is refused to a read-only session: every method but GET, HEAD and
    /// OPTIONS is, unless the operation is in <see cref="AllowedInReadOnly"/>. Default-deny, so a write
    /// endpoint added later is refused until it is allowed here on purpose; a request that matched no
    /// endpoint (<paramref name="template"/> <see langword="null"/>) is refused as well.
    /// </summary>
    internal static bool IsBlockedInReadOnlyMode(string method, string? template)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            return false;

        return template is null || !AllowedInReadOnly.Contains(Key(method, template));
    }

    private static string Key(string method, string template) =>
        string.Concat(method.ToUpperInvariant(), " ", template);

    private static async Task RefuseAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, new ErrorResponse(message), ApiJsonContext.Default.ErrorResponse);
    }
}
