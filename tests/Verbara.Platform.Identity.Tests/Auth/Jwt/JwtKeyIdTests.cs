using System.Security.Cryptography;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Identity.Tests.Auth.Jwt;

public sealed class JwtKeyIdTests
{
    private static string Token(string? kid)
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = kid };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "verbara-platform",
            Audience = "verbara-platform",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    [Fact]
    public void FromToken_ShouldReturnTheHeaderKid_WhenTheTokenNamesOne() =>
        JwtKeyId.FromToken(Token("k-2026-11-22")).Should().Be("k-2026-11-22");

    [Fact]
    public void FromToken_ShouldReturnNull_WhenTheHeaderHasNoKid() =>
        JwtKeyId.FromToken(Token(null)).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData(".payload.signature")]
    [InlineData("!!!!.payload.signature")]
    [InlineData("bm90LWpzb24.payload.signature")]          // "not-json"
    [InlineData("eyJraWQiOjQyfQ.payload.signature")]       // {"kid":42}
    [InlineData("eyJhbGciOiJIUzI1NiIsImtpZCI6.p.s")]       // truncated JSON
    public void FromToken_ShouldReturnNull_WhenTheTokenCannotBeRead(string? token) =>
        JwtKeyId.FromToken(token).Should().BeNull();

    [Fact]
    public void FromToken_ShouldSkipNestedValues_WhenTheKidFollowsThem() =>
        // {"x5c":["a",{"kid":"inner"}],"jwk":{"kid":"nested"},"kid":"outer"}
        JwtKeyId.FromToken(Base64UrlEncoder.Encode("""{"x5c":["a",{"kid":"inner"}],"jwk":{"kid":"nested"},"kid":"outer"}""") + ".p.s")
            .Should().Be("outer");

    [Fact]
    public void FromToken_ShouldReturnNull_WhenTheHeaderIsImplausiblyLong() =>
        JwtKeyId.FromToken(new string('e', 4096) + ".p.s").Should().BeNull();

    [Theory]
    [InlineData(1536, "edge")]      // 2048 encoded characters: the longest header read
    [InlineData(1537, null)]        // 2050 encoded characters
    public void FromToken_ShouldReadTheHeaderUpToTwoKilobytes_WhenItIsPadded(int headerBytes, string? expected)
    {
        const string prefix = "{\"kid\":\"edge\",\"pad\":\"";
        var header = prefix + new string('a', headerBytes - prefix.Length - 2) + "\"}";
        var encoded = Base64UrlEncoder.Encode(header);
        encoded.Length.Should().Be(headerBytes == 1536 ? 2048 : 2050);

        JwtKeyId.FromToken(encoded + ".p.s").Should().Be(expected);
    }

    [Fact]
    public void FromRequest_ShouldPreferTheQueryToken_WhenBothAreGiven() =>
        JwtKeyId.FromRequest(Token("query"), "Bearer " + Token("header")).Should().Be("query");

    [Theory]
    [InlineData("Bearer ")]
    [InlineData("bearer ")]
    [InlineData("BEARER  ")]
    public void FromRequest_ShouldReadTheAuthorizationHeader_WhenNoQueryTokenIsGiven(string scheme) =>
        JwtKeyId.FromRequest(null, scheme + Token("header")).Should().Be("header");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    public void FromRequest_ShouldReturnNull_WhenThereIsNoBearerToken(string? authorization) =>
        JwtKeyId.FromRequest(null, authorization).Should().BeNull();
}
