namespace Verbara.Platform.Realtime.Auth;

/// <summary>
/// X-Service-Key header validation middleware. Same shape as the inline
/// middleware in Verbara.Platform.Renderer (lines 28-46 of its Program.cs)
/// and Verbara.Platform.Mail. Extracted to a static method so Realtime's
/// Program.cs stays declarative.
/// </summary>
public static class ServiceKeyMiddleware
{
    /// <summary>Paths that bypass the service-key check.</summary>
    private static readonly string[] s_unauthenticatedPaths =
    [
        "/health",
        // SignalR hub path is auth'd by JWT, not service key (added in Phase A.2+A.3)
        "/hubs",
    ];

    public static IApplicationBuilder UseServiceKey(this IApplicationBuilder app, string expectedKey)
    {
        return app.Use(async (context, next) =>
        {
            foreach (var open in s_unauthenticatedPaths)
            {
                if (context.Request.Path.StartsWithSegments(open))
                {
                    await next();
                    return;
                }
            }

            var requestKey = context.Request.Headers["X-Service-Key"].FirstOrDefault();
            if (!string.Equals(requestKey, expectedKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or missing service key");
                return;
            }

            await next();
        });
    }
}
