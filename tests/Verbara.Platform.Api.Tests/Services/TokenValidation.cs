using System.Security.Claims;
using Verbara.Platform.Api.Services;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// Validates a token against a <see cref="JwtTokenService"/>'s
/// <see cref="JwtTokenService.ValidationParameters"/> with the handler the JwtBearer scheme uses
/// (no inbound claim mapping, as the scheme is configured): signature, issuer, audience and
/// lifetime, as a request would, after awaiting the validation keys' first load as the scheme's
/// <c>OnMessageReceived</c> event does. Revocation is not part of it — the JwtBearer
/// <c>OnTokenValidated</c> event does that, and the pipeline suites cover it.
/// </summary>
internal static class TokenValidation
{
    /// <summary>The validated principal, or <see langword="null"/> when the token does not validate.</summary>
    public static async Task<ClaimsPrincipal?> ValidateAsync(JwtTokenService service, string token)
    {
        // As the JwtBearer OnMessageReceived event does before every token check.
        await service.EnsureValidationKeysAsync(CancellationToken.None);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, service.ValidationParameters);
        return result.IsValid ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
    }

    /// <summary>
    /// <see cref="ValidateAsync"/>, passing the key id the token names, as the scheme's
    /// <c>OnMessageReceived</c> event does: a token signed with a key this replica does not hold yet
    /// waits for one reload.
    /// </summary>
    public static async Task<ClaimsPrincipal?> ValidateWithKeyIdAsync(JwtTokenService service, string token)
    {
        await service.EnsureValidationKeysAsync(
            Verbara.Platform.Identity.Auth.Jwt.JwtKeyId.FromRequest(null, "Bearer " + token), CancellationToken.None);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, service.ValidationParameters);
        return result.IsValid ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
    }
}
