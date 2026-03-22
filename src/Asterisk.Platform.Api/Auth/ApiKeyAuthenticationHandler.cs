using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Auth;

internal sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IApiKeyStore _apiKeyStore;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyStore apiKeyStore)
        : base(options, logger, encoder)
    {
        _apiKeyStore = apiKeyStore;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return AuthenticateResult.NoResult();

        var header = authHeader.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var rawKey = header["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(rawKey))
            return AuthenticateResult.Fail("Missing API key");

        var hashedKey = HashKey(rawKey);
        var apiKey = await _apiKeyStore.GetByHashAsync(hashedKey, Context.RequestAborted);

        if (apiKey is null)
            return AuthenticateResult.Fail("Invalid API key");

        if (apiKey.IsRevoked)
            return AuthenticateResult.Fail("API key has been revoked");

        if (apiKey.IsExpired(DateTimeOffset.UtcNow))
            return AuthenticateResult.Fail("API key has expired");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.KeyId.Value),
            new Claim("tenant_id", apiKey.TenantId.Value),
            new Claim("key_name", apiKey.Name),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        // Store resolved tenant in HttpContext so middleware can read it
        Context.Items["TenantId"] = new TenantId(apiKey.TenantId.Value);

        return AuthenticateResult.Success(ticket);
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
