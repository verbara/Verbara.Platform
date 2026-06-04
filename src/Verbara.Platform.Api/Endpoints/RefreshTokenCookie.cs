namespace Verbara.Platform.Api.Endpoints;

// Single source of truth for the refresh-token cookie. The Path MUST match the
// versioned route the SPA posts to (/api/v1/auth) or the browser won't send the
// cookie on refresh; Delete MUST use the same Path or it won't clear it.
internal static class RefreshTokenCookie
{
    internal const string Name = "refresh_token";
    internal const string Path = "/api/v1/auth";
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    internal static void Append(HttpContext context, string rawToken) =>
        context.Response.Cookies.Append(Name, rawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = Path,
            MaxAge = MaxAge,
        });

    internal static void Delete(HttpContext context) =>
        context.Response.Cookies.Delete(Name, new CookieOptions { Path = Path });
}
