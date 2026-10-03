using System.Security.Claims;
using Verbara.Platform.Api.Services;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// Validates a token against a <see cref="JwtTokenService"/>'s
/// <see cref="JwtTokenService.ValidationParameters"/> with the handler the JwtBearer scheme uses
/// (no inbound claim mapping, as the scheme is configured): signature, issuer, audience and
/// lifetime, as a request would. Revocation is not part of it — the JwtBearer
/// <c>OnTokenValidated</c> event does that, and the pipeline suites cover it.
/// </summary>
internal static class TokenValidation
{
    /// <summary>The validated principal, or <see langword="null"/> when the token does not validate.</summary>
    public static async Task<ClaimsPrincipal?> ValidateAsync(JwtTokenService service, string token)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, service.ValidationParameters);
        return result.IsValid ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
    }
}
