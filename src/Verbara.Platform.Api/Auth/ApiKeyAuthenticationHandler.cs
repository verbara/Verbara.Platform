using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Auth;

internal sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IApiKeyStore _apiKeyStore;
    private readonly IUserStore _userStore;
    private readonly IApiKeyLastUsedStamper? _lastUsedStamper;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyStore apiKeyStore,
        IUserStore userStore,
        IApiKeyLastUsedStamper? lastUsedStamper = null)
        : base(options, logger, encoder)
    {
        _apiKeyStore = apiKeyStore;
        _userStore = userStore;
        _lastUsedStamper = lastUsedStamper;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? apiKeyValue = null;

        if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var header = authHeader.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                apiKeyValue = header["Bearer ".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(apiKeyValue))
            return AuthenticateResult.NoResult();

        var hashedKey = HashKey(apiKeyValue);
        var apiKey = await _apiKeyStore.GetByHashAsync(hashedKey, Context.RequestAborted);

        if (apiKey is null)
            return AuthenticateResult.Fail("Invalid API key");

        if (apiKey.IsRevoked)
            return AuthenticateResult.Fail("API key has been revoked");

        if (apiKey.IsExpired(DateTimeOffset.UtcNow))
            return AuthenticateResult.Fail("API key has expired");

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.KeyId.Value),
            new Claim("tenant_id", apiKey.TenantId.Value),
            new Claim("key_name", apiKey.Name),
        };

        if (apiKey.KeyType == ApiKeyType.Management)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            claims.Add(new Claim("key_type", "management"));
        }

        if (apiKey.UserId is { } userId)
        {
            var user = await _userStore.GetByIdAsync(apiKey.TenantId, userId, Context.RequestAborted);
            if (user is not null)
            {
                claims.Add(new Claim(ClaimTypes.Role, user.Role.ToString()));
                claims.Add(new Claim("user_id", user.UserId.Value));
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        // Store resolved tenant in HttpContext so middleware can read it
        Context.Items["TenantId"] = new TenantId(apiKey.TenantId.Value);

        // Fire-and-forget stamp of last-used. Debounced in-process so this is
        // a no-op for callers that re-auth within the debounce window.
        // Latency-critical: we DO NOT await — auth latency must not depend on
        // a write to api_keys. (R5.2 PC.5 / B.12)
        if (_lastUsedStamper is not null)
        {
            _ = _lastUsedStamper.StampAsync(apiKey.KeyId, Context.RequestAborted);
        }

        return AuthenticateResult.Success(ticket);
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
