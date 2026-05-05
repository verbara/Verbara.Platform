namespace Verbara.Platform.Identity.OidcTokenExchange;

public interface IOidcTokenExchangeService
{
    Task<OidcTokenResponse> ExchangeCodeAsync(
        string authority, string code, string codeVerifier,
        string redirectUri, string clientId, string clientSecret,
        CancellationToken ct);

    Task<OidcClaimsResult> ValidateIdTokenAsync(
        string idToken, string authority, string expectedAudience,
        string expectedNonce, CancellationToken ct);
}
