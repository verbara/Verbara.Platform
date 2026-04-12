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

                    // Check for JWT in query string (SSE — EventSource can't set headers)
                    var queryToken = context.Request.Query["token"].FirstOrDefault();
                    if (queryToken is not null && queryToken.StartsWith("eyJ", StringComparison.Ordinal))
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
                        var token = context.Request.Query["token"].FirstOrDefault();
                        if (token is not null && token.StartsWith("eyJ", StringComparison.Ordinal))
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
}
