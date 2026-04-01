using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asterisk.Platform.Identity.OidcTokenExchange;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Asterisk.Platform.Identity.Tests;

public sealed class OidcTokenExchangeServiceTests
{
    // --- PKCE Tests ---

    [Fact]
    public void GenerateCodeVerifier_ShouldReturnBase64UrlString()
    {
        var verifier = OidcTokenExchangeService.GenerateCodeVerifier();

        verifier.Should().NotBeNullOrWhiteSpace();
        verifier.Length.Should().BeGreaterOrEqualTo(43);
        verifier.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Fact]
    public void GenerateCodeVerifier_ShouldProduceUniqueValues()
    {
        var v1 = OidcTokenExchangeService.GenerateCodeVerifier();
        var v2 = OidcTokenExchangeService.GenerateCodeVerifier();

        v1.Should().NotBe(v2);
    }

    [Fact]
    public void ComputeCodeChallenge_ShouldReturnDeterministicS256Hash()
    {
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var challenge = OidcTokenExchangeService.ComputeCodeChallenge(verifier);

        // RFC 7636 Appendix B test vector
        challenge.Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void GenerateNonce_ShouldReturnUniqueBase64UrlString()
    {
        var n1 = OidcTokenExchangeService.GenerateNonce();
        var n2 = OidcTokenExchangeService.GenerateNonce();

        n1.Should().NotBeNullOrWhiteSpace();
        n2.Should().NotBeNullOrWhiteSpace();
        n1.Should().NotBe(n2);
    }

    // --- Token Exchange Tests ---

    [Fact]
    public async Task ExchangeCodeAsync_ShouldPostToTokenEndpoint()
    {
        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var tokenResponseJson = JsonSerializer.Serialize(new OidcTokenResponse
        {
            AccessToken = "at-123",
            IdToken = "id-token-jwt",
            TokenType = "Bearer",
            ExpiresIn = 3600,
        }, OidcJsonContext.Default.OidcTokenResponse);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/oauth/token"] = tokenResponseJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var result = await service.ExchangeCodeAsync(
            "https://idp.example.com",
            "auth-code-123",
            "code-verifier-abc",
            "https://app.example.com/callback",
            "client-id",
            "client-secret",
            CancellationToken.None);

        result.AccessToken.Should().Be("at-123");
        result.IdToken.Should().Be("id-token-jwt");
    }

    [Fact]
    public async Task ExchangeCodeAsync_ShouldThrow_WhenIdpReturnsError()
    {
        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var errorJson = JsonSerializer.Serialize(new OidcTokenResponse
        {
            Error = "invalid_grant",
            ErrorDescription = "The authorization code has expired",
        }, OidcJsonContext.Default.OidcTokenResponse);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/oauth/token"] = errorJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var act = () => service.ExchangeCodeAsync(
            "https://idp.example.com",
            "expired-code",
            "verifier",
            "https://app.example.com/callback",
            "client-id",
            "client-secret",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid_grant*");
    }

    // --- ID Token Validation Tests ---

    [Fact]
    public async Task ValidateIdTokenAsync_ShouldExtractClaims_WhenTokenIsValid()
    {
        var rsa = RSA.Create(2048);
        var kid = "test-key-1";
        var nonce = "test-nonce-123";

        var idToken = CreateTestIdToken(rsa, kid, nonce,
            issuer: "https://idp.example.com",
            audience: "client-id",
            email: "user@example.com",
            subject: "oidc-sub-abc",
            name: "Test User");

        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var jwksJson = CreateJwksJson(rsa, kid);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/.well-known/jwks.json"] = jwksJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var claims = await service.ValidateIdTokenAsync(
            idToken, "https://idp.example.com", "client-id", nonce, CancellationToken.None);

        claims.Subject.Should().Be("oidc-sub-abc");
        claims.Email.Should().Be("user@example.com");
        claims.Name.Should().Be("Test User");
    }

    [Fact]
    public async Task ValidateIdTokenAsync_ShouldThrow_WhenNonceMismatch()
    {
        var rsa = RSA.Create(2048);
        var kid = "test-key-1";

        var idToken = CreateTestIdToken(rsa, kid, "actual-nonce",
            issuer: "https://idp.example.com",
            audience: "client-id",
            email: "user@example.com",
            subject: "sub-1");

        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var jwksJson = CreateJwksJson(rsa, kid);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/.well-known/jwks.json"] = jwksJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var act = () => service.ValidateIdTokenAsync(
            idToken, "https://idp.example.com", "client-id", "wrong-nonce", CancellationToken.None);

        await act.Should().ThrowAsync<SecurityTokenValidationException>()
            .WithMessage("*nonce*");
    }

    [Fact]
    public async Task ValidateIdTokenAsync_ShouldThrow_WhenAudienceMismatch()
    {
        var rsa = RSA.Create(2048);
        var kid = "test-key-1";
        var nonce = "test-nonce";

        var idToken = CreateTestIdToken(rsa, kid, nonce,
            issuer: "https://idp.example.com",
            audience: "wrong-client-id",
            email: "user@example.com",
            subject: "sub-1");

        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);

        var jwksJson = CreateJwksJson(rsa, kid);

        var handler = new MockHttpHandler(new Dictionary<string, string>
        {
            ["https://idp.example.com/.well-known/openid-configuration"] = discoveryJson,
            ["https://idp.example.com/.well-known/jwks.json"] = jwksJson,
        });

        var factory = new MockHttpClientFactory(handler);
        var service = new OidcTokenExchangeService(factory, NullLogger<OidcTokenExchangeService>.Instance);

        var act = () => service.ValidateIdTokenAsync(
            idToken, "https://idp.example.com", "correct-client-id", nonce, CancellationToken.None);

        await act.Should().ThrowAsync<SecurityTokenValidationException>();
    }

    // --- Test Helpers ---

    private static string CreateTestIdToken(
        RSA rsa, string kid, string nonce,
        string issuer, string audience, string email, string subject, string? name = null)
    {
        var securityKey = new RsaSecurityKey(rsa) { KeyId = kid };
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Email, email),
            new("nonce", nonce),
            new("email_verified", "true"),
        };
        if (name is not null)
            claims.Add(new Claim("name", name));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(1),
            IssuedAt = DateTime.UtcNow,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = credentials,
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        return tokenHandler.CreateEncodedJwt(descriptor);
    }

    private static string CreateJwksJson(RSA rsa, string kid)
    {
        var parameters = rsa.ExportParameters(false);
        var n = Base64UrlEncode(parameters.Modulus!);
        var e = Base64UrlEncode(parameters.Exponent!);

        return $$"""
        {
          "keys": [
            {
              "kty": "RSA",
              "use": "sig",
              "kid": "{{kid}}",
              "alg": "RS256",
              "n": "{{n}}",
              "e": "{{e}}"
            }
          ]
        }
        """;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// --- Test doubles ---

internal sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;

    public MockHttpHandler(Dictionary<string, string> responses) => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        // For POST requests (token endpoint), match by URL without query
        var matchUrl = request.Method == HttpMethod.Post
            ? request.RequestUri.GetLeftPart(UriPartial.Path)
            : url;

        if (_responses.TryGetValue(matchUrl, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class MockHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public MockHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
