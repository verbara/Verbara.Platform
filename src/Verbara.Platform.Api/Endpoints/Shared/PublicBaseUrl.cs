using System.Text.RegularExpressions;
using Verbara.Platform.Core.Branding;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// The address users open the console at, which the links that bring a user back to it are built
/// from: the password-reset link mailed by <c>POST /auth/forgot-password</c> and the OIDC
/// <c>redirect_uri</c>. An OIDC sign-in's <c>return_url</c> must be on its origin.
/// </summary>
/// <remarks>
/// <para>
/// It comes from configuration, never from the request. The Host header, and X-Forwarded-Host,
/// carry whatever the sender writes: a link built from them points wherever the sender chooses,
/// which for the reset link means mailing a user's single-use token to that place.
/// </para>
/// <para>
/// <c>Platform:PublicBaseUrl</c> wins. Without it, the origin <c>CORS_ORIGINS</c> names when it
/// names exactly one, as the documented production configuration does, so such a deployment needs
/// no new setting. With neither there is no address that is safe to use: the reset email is not
/// sent and OIDC sign-in is refused, each with a warning that names the setting. A
/// <c>Platform:PublicBaseUrl</c> that is set but is not an absolute http(s) URL without query,
/// fragment or user information counts as missing; it does not fall back to <c>CORS_ORIGINS</c>,
/// because the operator meant another address.
/// </para>
/// <para>
/// An installation whose tenants open the console at their own host names that host in the setting
/// with <c>{tenant}</c> (<c>https://{tenant}.example.com</c>). It is filled with the host label the
/// tenant is reached at, as <c>TenantResolutionMiddleware</c> resolves it back: the tenant's branding
/// subdomain, or else its id. The tenant is the one the server already trusts (the user's, or the
/// OIDC flow's), never the request's Host, and the label must be a lowercase DNS label, so the address
/// stays under the configured domain.
/// </para>
/// </remarks>
internal static partial class PublicBaseUrl
{
    /// <summary>The setting (environment variable <c>Platform__PublicBaseUrl</c>).</summary>
    internal const string ConfigurationKey = "Platform:PublicBaseUrl";

    /// <summary>Stands, in the setting, for the host label of the tenant a link is built for.</summary>
    internal const string TenantPlaceholder = "{tenant}";

    private const string CorsOriginsKey = "CORS_ORIGINS";
    private const string LoggerCategory = "Verbara.Platform.Api.PublicBaseUrl";

    /// <summary>A label that stands in for a tenant's when checking a setting that names <see cref="TenantPlaceholder"/>.</summary>
    private const string SampleTenantLabel = "tenant";

    /// <summary>The console's reset-password page.</summary>
    private const string ResetPasswordPath = "/reset-password";

    /// <summary>
    /// The OIDC callback, unversioned as it has always been sent: identity providers hold it as a
    /// registered redirect URI, and the version alias routes it to <c>/api/v1</c>.
    /// </summary>
    private const string OidcCallbackPath = "/api/auth/oidc/callback";

    /// <summary>
    /// The configured address — scheme, host, port and any path, without a trailing slash — or
    /// <see langword="null"/> when none is usable. A <see cref="TenantPlaceholder"/> in the setting is
    /// filled with <paramref name="tenantLabel"/>, and without a usable label there is no address.
    /// </summary>
    internal static string? Resolve(IConfiguration configuration, string? tenantLabel = null)
    {
        var configured = configuration[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!configured.Contains(TenantPlaceholder, StringComparison.Ordinal))
                return Normalize(configured);

            return tenantLabel is not null && HostLabel().IsMatch(tenantLabel)
                ? Normalize(configured.Replace(TenantPlaceholder, tenantLabel, StringComparison.Ordinal))
                : null;
        }

        var origins = configuration[CorsOriginsKey]?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return origins is [var single] && single != "*" ? Normalize(single) : null;
    }

    /// <summary>
    /// Why no address resolves, or <see langword="null"/> when one does. A setting that names
    /// <see cref="TenantPlaceholder"/> resolves when it is usable once a label fills it.
    /// </summary>
    internal static string? UnresolvableReason(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration[ConfigurationKey]))
            return Resolve(configuration, SampleTenantLabel) is null
                ? $"{ConfigurationKey} is not an absolute http(s) URL without query, fragment or user information"
                : null;

        var origins = configuration[CorsOriginsKey]?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return origins switch
        {
            [] => $"{ConfigurationKey} is not set, and neither is {CorsOriginsKey}",
            ["*"] => $"{ConfigurationKey} is not set, and {CorsOriginsKey} is '*'",
            [var single] => Normalize(single) is null
                ? $"{ConfigurationKey} is not set, and {CorsOriginsKey}' one origin is not an absolute http(s) URL"
                : null,
            _ => $"{ConfigurationKey} is not set, and {CorsOriginsKey} names {origins.Length} origins",
        };
    }

    /// <summary>
    /// Records, once at startup, that reset links and OIDC sign-in are off because no address resolves;
    /// nothing when one does.
    /// </summary>
    internal static void LogIfUnresolvable(ILoggerFactory loggerFactory, IConfiguration configuration)
    {
        if (UnresolvableReason(configuration) is { } reason)
            LogNoPublicBaseUrl(loggerFactory.CreateLogger(LoggerCategory), reason,
                configuration[ConfigurationKey], configuration[CorsOriginsKey]);
    }

    /// <summary>
    /// The address for the users of <paramref name="tenantId"/>, a tenant the caller already trusts:
    /// <see cref="Resolve"/>, with a <see cref="TenantPlaceholder"/> filled with the label the tenant is
    /// reached at (its branding subdomain, or else its id, whichever is a lowercase DNS label first).
    /// </summary>
    internal static async ValueTask<string?> ResolveForTenantAsync(
        HttpContext context, IConfiguration configuration, string tenantId, CancellationToken ct)
    {
        if (configuration[ConfigurationKey]?.Contains(TenantPlaceholder, StringComparison.Ordinal) != true)
            return Resolve(configuration);

        var branding = context.RequestServices.GetService<ITenantBrandingStore>();
        var subdomain = branding is null ? null : (await branding.GetAsync(tenantId, ct))?.Subdomain;
        var label = subdomain is not null && HostLabel().IsMatch(subdomain) ? subdomain : tenantId;
        return Resolve(configuration, label);
    }

    /// <summary>
    /// The reset link for <paramref name="token"/> under <paramref name="baseUrl"/>. The token is
    /// percent-encoded: it is standard Base64, and a browser reads a bare <c>+</c> in a query
    /// string as a space, which turned it into a token no cache holds.
    /// </summary>
    internal static string ResetPasswordLink(string baseUrl, string token) =>
        $"{baseUrl}{ResetPasswordPath}?token={Uri.EscapeDataString(token)}";

    /// <summary>
    /// The OIDC <c>redirect_uri</c> for <paramref name="baseUrl"/>: the callback at its origin. A path in
    /// the address is the console's, not the API's (the reference gateway routes <c>/api/</c> at the
    /// host's root), and the redirect URI has always been built from the scheme and host alone.
    /// </summary>
    internal static string OidcRedirectUri(string baseUrl) =>
        new Uri(baseUrl).GetLeftPart(UriPartial.Authority) + OidcCallbackPath;

    /// <summary>
    /// Where a completed sign-in may send the browser: <paramref name="returnUrl"/> when it is a path on
    /// the console (a single <c>/</c> first) or an absolute URL whose scheme, host and port are those of
    /// <paramref name="baseUrl"/>, and <c>/</c> otherwise.
    /// </summary>
    /// <remarks>
    /// The sign-in's result travels in the fragment of that URL (an access token, an MFA challenge
    /// token, the user's email), and the fragment is readable by whatever origin the URL names. The
    /// <c>return_url</c> is chosen by whoever sends the user a link to the login endpoint, so it may not
    /// name another origin. Hosts are compared as parsed URI components, never as string prefixes
    /// (<c>https://console.example.test.attacker.example</c> begins with <c>https://console.example.test</c>),
    /// and an accepted absolute URL is returned in its parsed form. Whitespace, control characters and
    /// backslashes are refused outright: browsers drop or rewrite them before resolving a URL, which
    /// turns <c>/\host</c> or <c>/&lt;tab&gt;/host</c> into a URL on another host.
    /// </remarks>
    internal static string SignInReturnUrl(string? returnUrl, string baseUrl)
    {
        const string ConsoleRoot = "/";
        if (string.IsNullOrEmpty(returnUrl)
            || returnUrl.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\'))
            return ConsoleRoot;

        // A path: "//host" is a URL on another host, not a path.
        if (returnUrl[0] == '/')
            return returnUrl.Length > 1 && returnUrl[1] == '/' ? ConsoleRoot : returnUrl;

        return Uri.TryCreate(returnUrl, UriKind.Absolute, out var target)
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var console)
            && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp)
            && target.UserInfo.Length == 0
            && string.Equals(target.Scheme, console.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.IdnHost, console.IdnHost, StringComparison.OrdinalIgnoreCase)
            && target.Port == console.Port
                ? target.AbsoluteUri
                : ConsoleRoot;
    }

    /// <summary>Records that a reset email for <paramref name="tenantId"/> was not sent because no address is usable.</summary>
    internal static void LogResetEmailNotSent(HttpContext context, IConfiguration configuration, string tenantId) =>
        LogResetEmailNotSent(Logger(context), tenantId, configuration[ConfigurationKey], configuration[CorsOriginsKey]);

    /// <summary>Records that an OIDC sign-in step for <paramref name="tenantId"/> was refused because no address is usable.</summary>
    internal static void LogOidcSignInRefused(HttpContext context, IConfiguration configuration, string tenantId) =>
        LogOidcSignInRefused(Logger(context), tenantId, configuration[ConfigurationKey], configuration[CorsOriginsKey]);

    private static string? Normalize(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.UserInfo.Length > 0)
            return null;

        return (uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath).TrimEnd('/');
    }

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

    /// <summary>A lowercase DNS label: what a host's first label can be, and nothing that leaves the domain.</summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostLabel();

    [LoggerMessage(EventId = 7520, Level = LogLevel.Warning,
        Message = "Password-reset email not sent for tenant '{TenantId}': no usable public base URL. Set Platform:PublicBaseUrl to the absolute http(s) address users open the console at, where {{tenant}} stands for the tenant's branding subdomain or, without one, its id, which must be a lowercase DNS label; CORS_ORIGINS stands in only when it names exactly one origin. Platform:PublicBaseUrl='{PublicBaseUrl}', CORS_ORIGINS='{CorsOrigins}'.")]
    private static partial void LogResetEmailNotSent(ILogger logger, string tenantId, string? publicBaseUrl, string? corsOrigins);

    [LoggerMessage(EventId = 7522, Level = LogLevel.Warning,
        Message = "No usable public base URL ({Reason}): password-reset emails are not sent and OIDC sign-in is refused. Set Platform:PublicBaseUrl to the absolute http(s) address users open the console at; CORS_ORIGINS stands in only when it names exactly one origin. Platform:PublicBaseUrl='{PublicBaseUrl}', CORS_ORIGINS='{CorsOrigins}'.")]
    private static partial void LogNoPublicBaseUrl(ILogger logger, string reason, string? publicBaseUrl, string? corsOrigins);

    [LoggerMessage(EventId = 7521, Level = LogLevel.Warning,
        Message = "OIDC sign-in refused for tenant '{TenantId}': no usable public base URL to build the redirect_uri from. Set Platform:PublicBaseUrl to the absolute http(s) address users open the console at, where {{tenant}} stands for the tenant's branding subdomain or, without one, its id, which must be a lowercase DNS label; CORS_ORIGINS stands in only when it names exactly one origin. Platform:PublicBaseUrl='{PublicBaseUrl}', CORS_ORIGINS='{CorsOrigins}'.")]
    private static partial void LogOidcSignInRefused(ILogger logger, string tenantId, string? publicBaseUrl, string? corsOrigins);
}
