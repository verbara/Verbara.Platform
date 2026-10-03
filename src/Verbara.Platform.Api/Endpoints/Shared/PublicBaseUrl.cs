namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// The address users open the console at, which the links that bring a user back to it are built
/// from: the password-reset link mailed by <c>POST /auth/forgot-password</c> and the OIDC
/// <c>redirect_uri</c>.
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
/// </remarks>
internal static partial class PublicBaseUrl
{
    /// <summary>The setting (environment variable <c>Platform__PublicBaseUrl</c>).</summary>
    internal const string ConfigurationKey = "Platform:PublicBaseUrl";

    private const string CorsOriginsKey = "CORS_ORIGINS";
    private const string LoggerCategory = "Verbara.Platform.Api.PublicBaseUrl";

    /// <summary>The console's reset-password page.</summary>
    private const string ResetPasswordPath = "/reset-password";

    /// <summary>
    /// The OIDC callback, unversioned as it has always been sent: identity providers hold it as a
    /// registered redirect URI, and the version alias routes it to <c>/api/v1</c>.
    /// </summary>
    private const string OidcCallbackPath = "/api/auth/oidc/callback";

    /// <summary>
    /// The configured address — scheme, host, port and any path, without a trailing slash — or
    /// <see langword="null"/> when none is usable.
    /// </summary>
    internal static string? Resolve(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return Normalize(configured);

        var origins = configuration[CorsOriginsKey]?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return origins is [var single] && single != "*" ? Normalize(single) : null;
    }

    /// <summary>The reset link for <paramref name="token"/> under <paramref name="baseUrl"/>.</summary>
    internal static string ResetPasswordLink(string baseUrl, string token) =>
        $"{baseUrl}{ResetPasswordPath}?token={token}";

    /// <summary>The OIDC <c>redirect_uri</c> under <paramref name="baseUrl"/>.</summary>
    internal static string OidcRedirectUri(string baseUrl) => baseUrl + OidcCallbackPath;

    /// <summary>Records that a reset email was not sent because no address is usable.</summary>
    internal static void LogResetEmailNotSent(HttpContext context, IConfiguration configuration) =>
        LogResetEmailNotSent(Logger(context), configuration[ConfigurationKey], configuration[CorsOriginsKey]);

    /// <summary>Records that an OIDC sign-in step was refused because no address is usable.</summary>
    internal static void LogOidcSignInRefused(HttpContext context, IConfiguration configuration) =>
        LogOidcSignInRefused(Logger(context), configuration[ConfigurationKey], configuration[CorsOriginsKey]);

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

    [LoggerMessage(EventId = 7520, Level = LogLevel.Warning,
        Message = "Password-reset email not sent: no usable public base URL. Set Platform:PublicBaseUrl to the absolute http(s) address users open the console at; CORS_ORIGINS stands in only when it names exactly one origin. Platform:PublicBaseUrl='{PublicBaseUrl}', CORS_ORIGINS='{CorsOrigins}'.")]
    private static partial void LogResetEmailNotSent(ILogger logger, string? publicBaseUrl, string? corsOrigins);

    [LoggerMessage(EventId = 7521, Level = LogLevel.Warning,
        Message = "OIDC sign-in refused: no usable public base URL to build the redirect_uri from. Set Platform:PublicBaseUrl to the absolute http(s) address users open the console at; CORS_ORIGINS stands in only when it names exactly one origin. Platform:PublicBaseUrl='{PublicBaseUrl}', CORS_ORIGINS='{CorsOrigins}'.")]
    private static partial void LogOidcSignInRefused(ILogger logger, string? publicBaseUrl, string? corsOrigins);
}
