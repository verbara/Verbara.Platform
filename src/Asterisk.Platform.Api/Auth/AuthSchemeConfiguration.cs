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

    public static AuthenticationBuilder AddDynamicAuth(
        this IServiceCollection services,
        JwtTokenService jwtTokenService)
    {
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

                    // Check for JWT in query string — both `token` (legacy SSE) and
                    // `access_token` (SignalR client default) are accepted.
                    var queryToken = ExtractJwtQueryParam(context.Request);
                    if (queryToken is not null)
                        return JwtScheme;

                    return ApiKeyScheme;
                };
            })
            .AddJwtBearer(JwtScheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = jwtTokenService.ValidationParameters;

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = ExtractJwtQueryParam(context.Request);
                        if (token is not null)
                            context.Token = token;
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
}
