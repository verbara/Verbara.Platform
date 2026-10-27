namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Where the tenant in <c>HttpContext.Items["TenantId"]</c> came from (ADR-0038). Every writer of that
/// item records its source beside it, so a later reader — the tenant-boundary 403 — can say what was
/// resolved without recomputing the precedence.
/// </summary>
internal enum TenantSource
{
    /// <summary>Inbound channel webhook path (<c>/api/webhooks/{tenantId}/...</c>).</summary>
    WebhookPath,

    /// <summary>First label of the request host (white-label mapping or backward-compat fallback).</summary>
    Subdomain,

    /// <summary>The <c>X-Tenant-Id</c> request header.</summary>
    Header,

    /// <summary>The JWT <c>tid</c> claim, applied only when nothing earlier resolved a tenant.</summary>
    JwtFallback,

    /// <summary>The tenant an API key is bound to; overwrites whatever was resolved before.</summary>
    ApiKeyBinding,
}

/// <summary>
/// Single definition of the <c>Items["TenantIdSource"]</c> key, its pre-boxed values and the stable
/// wire tokens, shared by the four writers and the boundary middleware.
/// </summary>
internal static class TenantSourceItems
{
    public const string ItemKey = "TenantIdSource";

    // One box per member, so recording the source allocates nothing per request.
    private static readonly object BoxedWebhookPath = TenantSource.WebhookPath;
    private static readonly object BoxedSubdomain = TenantSource.Subdomain;
    private static readonly object BoxedHeader = TenantSource.Header;
    private static readonly object BoxedJwtFallback = TenantSource.JwtFallback;
    private static readonly object BoxedApiKeyBinding = TenantSource.ApiKeyBinding;

    public static void Set(HttpContext context, TenantSource source)
        => context.Items[ItemKey] = Box(source);

    public static bool TryGet(HttpContext context, out TenantSource source)
    {
        if (context.Items.TryGetValue(ItemKey, out var raw) && raw is TenantSource value)
        {
            source = value;
            return true;
        }

        source = default;
        return false;
    }

    // CS8524 (an unnamed, cast-only enum value is unhandled) is suppressed for both switches below and
    // ONLY it: a discard arm would silence it too, but would also swallow CS8509, the error that fires
    // when a NAMED member has no arm — the guarantee that adding a TenantSource fails the build until
    // it has a token and a box. Values only ever come from the named members; a cast-forged one would
    // throw SwitchExpressionException.
#pragma warning disable CS8524

    /// <summary>
    /// Stable lowercase token for a source, as named in diagnostics. A switch expression rather than
    /// <c>ToString()</c>: AOT-safe, and a missing arm for a named member fails the build
    /// (CS8509 under TreatWarningsAsErrors).
    /// </summary>
    public static string ToToken(TenantSource source) => source switch
    {
        TenantSource.WebhookPath => "webhook-path",
        TenantSource.Subdomain => "subdomain",
        TenantSource.Header => "header",
        TenantSource.JwtFallback => "jwt",
        TenantSource.ApiKeyBinding => "api-key",
    };

    private static object Box(TenantSource source) => source switch
    {
        TenantSource.WebhookPath => BoxedWebhookPath,
        TenantSource.Subdomain => BoxedSubdomain,
        TenantSource.Header => BoxedHeader,
        TenantSource.JwtFallback => BoxedJwtFallback,
        TenantSource.ApiKeyBinding => BoxedApiKeyBinding,
    };

#pragma warning restore CS8524
}
