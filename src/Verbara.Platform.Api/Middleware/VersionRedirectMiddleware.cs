namespace Verbara.Platform.Api.Middleware;

internal sealed class VersionRedirectMiddleware
{
    private readonly RequestDelegate _next;

    public VersionRedirectMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip if already versioned, not an API path, or is openapi/health
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/openapi", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Rewrite /api/foo → /api/v1/foo
        var newPath = "/api/v1" + path[4..];
        context.Request.Path = new PathString(newPath);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["Warning"] =
                "299 - \"Unversioned URL deprecated, use /api/v1/ prefix\"";
            return Task.CompletedTask;
        });

        await _next(context);
    }
}
