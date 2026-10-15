namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// The stable refusal codes <see cref="Middleware.TenantStatusMiddleware"/> writes for a tenant that is not
/// serving, in Platform's <see cref="ErrorResponse"/> convention: <c>error</c> carries only a kebab-case machine
/// code. Both answer <c>403</c>.
/// </summary>
/// <remarks>
/// The codes are written only on routes that opt in with
/// <see cref="TenantStatusRefusalEndpointExtensions.AnswersTenantRefusalWithCode"/> — the sign-in routes, whose
/// consumer maps them. Every other route keeps the prose body for the same refusal until its consumers map the
/// code too.
/// </remarks>
internal static class TenantStatusRefusals
{
    /// <summary>403: the tenant the request names is suspended.</summary>
    public const string Suspended = "tenant-suspended";

    /// <summary>403: the tenant the request names is pending deletion.</summary>
    public const string PendingDeletion = "tenant-pending-deletion";
}

/// <summary>
/// Endpoint metadata marking a route whose tenant-status refusal carries a <see cref="TenantStatusRefusals"/>
/// code instead of prose. Read by <see cref="Middleware.TenantStatusMiddleware"/> through
/// <c>GetMetadata&lt;T&gt;()</c>.
/// </summary>
internal sealed class TenantRefusalCodeMetadata
{
    public static readonly TenantRefusalCodeMetadata Instance = new();

    private TenantRefusalCodeMetadata()
    {
    }
}

internal static class TenantStatusRefusalEndpointExtensions
{
    /// <summary>
    /// Opts this route into the coded tenant-status refusal: a suspended or pending-deletion tenant is answered
    /// with <c>403 {"error":"tenant-suspended"}</c> or <c>{"error":"tenant-pending-deletion"}</c>. Applied only
    /// to <c>/auth/login</c>, <c>/auth/login/apikey</c> and <c>/auth/mfa/verify</c>.
    /// </summary>
    public static RouteHandlerBuilder AnswersTenantRefusalWithCode(this RouteHandlerBuilder builder)
    {
        builder.WithMetadata(TenantRefusalCodeMetadata.Instance);
        return builder;
    }
}
