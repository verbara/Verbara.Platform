using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Asterisk.Platform.Api.Auth;

internal static class AuthSchemeConfiguration
{
    public const string DynamicSelector = "DynamicSelector";
    public const string JwtScheme = "JwtScheme";
    public const string ApiKeyScheme = "ApiKey";

    public static AuthenticationBuilder AddDynamicAuth(this IServiceCollection services)
    {
        // Wire JWT validation parameters via post-configure so JwtTokenService is resolved
        // from DI (it requires IDataProtectionProvider, registered as a factory singleton).
        services.AddOptions<JwtBearerOptions>(JwtScheme)
            .Configure<JwtTokenService>((options, jwt) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = jwt.ValidationParameters;
            });

        return services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = DynamicSelector;
                options.DefaultChallengeScheme = DynamicSelector;
            })
            .AddPolicyScheme(DynamicSelector, DynamicSelector, options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                    if (authHeader is not null)
                    {
                        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            ? authHeader["Bearer ".Length..].Trim()
                            : authHeader.Trim();

                        if (token.StartsWith("eyJ", StringComparison.Ordinal))
                            return JwtScheme;
                    }

                    // v1.14.4 (AUTH-002 fix) — query-string tokens are
                    // accepted ONLY for paths that legitimately can't carry
                    // an Authorization header (SignalR hubs, EventSource SSE,
                    // <audio> recording streams). For every other path the
                    // query-string token is silently ignored — the request
                    // falls through to the API-key scheme. This prevents
                    // tokens from leaking via access logs / browser history /
                    // Referer headers on regular API calls.
                    if (IsQueryTokenPathAllowed(context.Request.Path))
                    {
                        var queryToken = ExtractJwtQueryParam(context.Request);
                        if (queryToken is not null)
                            return JwtScheme;
                    }

                    return ApiKeyScheme;
                };
            })
            .AddJwtBearer(JwtScheme, options =>
            {
                // MapInboundClaims + TokenValidationParameters are set via post-configure above.
                // Events are wired here.

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // v1.14.4 (AUTH-002 fix) — same path-scoping as the
                        // ForwardDefaultSelector above. Without this guard,
                        // a `?access_token=` on any endpoint would still be
                        // honored once the request reached the JwtBearer
                        // handler.
                        if (IsQueryTokenPathAllowed(context.Request.Path))
                        {
                            var token = ExtractJwtQueryParam(context.Request);
                            if (token is not null)
                                context.Token = token;
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        // Only set tenant from JWT if middleware didn't already resolve one
                        // (X-Tenant-Id header / subdomain takes precedence for cross-tenant access)
                        if (!context.HttpContext.Items.ContainsKey("TenantId"))
                        {
                            var tenantClaim = context.Principal?.FindFirst("tid")?.Value;
                            if (tenantClaim is not null)
                                context.HttpContext.Items["TenantId"] = new TenantId(tenantClaim);
                        }

                        return Task.CompletedTask;
                    },
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyScheme, _ => { });
    }

    /// <summary>
    /// Returns the JWT carried in either the <c>?token=</c> (legacy SSE endpoint)
    /// or <c>?access_token=</c> (SignalR / @microsoft/signalr default) query
    /// parameter, only when it looks like a JWT (starts with <c>eyJ</c>).
    /// </summary>
    private static string? ExtractJwtQueryParam(HttpRequest request)
    {
        var token = request.Query["token"].FirstOrDefault()
            ?? request.Query["access_token"].FirstOrDefault();
        return token is not null && token.StartsWith("eyJ", StringComparison.Ordinal)
            ? token
            : null;
    }

    /// <summary>
    /// v1.14.4 (AUTH-002 fix) — whitelist of path prefixes where the
    /// browser/client legitimately cannot set an <c>Authorization</c> header
    /// and must fall back to a query-string token. All other endpoints
    /// reject query-string tokens to prevent them from leaking via access
    /// logs, browser history, and Referer headers.
    ///
    /// <list type="bullet">
    ///   <item><description><c>/hubs/**</c> — SignalR (WebSocket handshake;
    ///         <c>@microsoft/signalr</c> default is <c>?access_token=</c>).</description></item>
    ///   <item><description><c>/events/stream**</c> — Server-Sent Events
    ///         (browser <c>EventSource</c> can't set headers).</description></item>
    ///   <item><description><c>/api/v*/recordings/{sessionId}/stream</c> —
    ///         audio playback via <c>&lt;audio&gt;</c> element (same header
    ///         constraint as <c>EventSource</c>).</description></item>
    /// </list>
    /// </summary>
    internal static bool IsQueryTokenPathAllowed(PathString path)
    {
        if (!path.HasValue)
            return false;

        var p = path.Value!;
        return p.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/events/stream", StringComparison.OrdinalIgnoreCase)
            || IsRecordingStreamPath(p);
    }

    /// <summary>
    /// Matches <c>/api/{anything}/recordings/{anything}/stream</c> without
    /// allocating a regex.
    /// </summary>
    private static bool IsRecordingStreamPath(string path)
    {
        // Must end with `/stream` and contain `/recordings/`.
        if (!path.EndsWith("/stream", StringComparison.OrdinalIgnoreCase))
            return false;
        var idx = path.IndexOf("/recordings/", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0)
            return false;
        // Require an `/api/` prefix segment somewhere before /recordings/.
        return path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    }
}
