using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.IdentityModel.Tokens;

namespace Asterisk.Platform.Api.Tests.Services;

public sealed class JwtTokenServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JwtTokenService _sut;

    public JwtTokenServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jwt-test-{Guid.NewGuid():N}");
        _sut = new JwtTokenService(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static User MakeUser() => new()
    {
        UserId = EntityId.From("user1"),
        TenantId = new TenantId("t1"),
        Email = "admin@example.com",
        DisplayName = "Admin User",
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void GenerateAccessToken_ShouldReturnValidJwt()
    {
        var (token, expiresAt) = _sut.GenerateAccessToken(MakeUser());

        token.Should().NotBeNullOrWhiteSpace();
        expiresAt.Should().BeAfter(DateTimeOffset.UtcNow);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Issuer.Should().Be("asterisk-platform");
        jwt.Audiences.Should().Contain("asterisk-platform");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "user1");
        jwt.Claims.Should().Contain(c => c.Type == "tid" && c.Value == "t1");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Email && c.Value == "admin@example.com");
        jwt.Claims.Should().Contain(c => c.Type == "name" && c.Value == "Admin User");
    }

    [Fact]
    public void ValidateToken_ShouldReturnPrincipal_WhenTokenIsValid()
    {
        var (token, _) = _sut.GenerateAccessToken(MakeUser());

        var principal = _sut.ValidateToken(token);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtRegisteredClaimNames.Sub)?.Value.Should().Be("user1");
    }

    [Fact]
    public void ValidateToken_ShouldReturnNull_WhenTokenIsInvalid()
    {
        var result = _sut.ValidateToken("not-a-valid-jwt");

        result.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_ShouldReturnNull_WhenTokenSignedByDifferentKey()
    {
        // Generate a token with a different key
        var otherDir = Path.Combine(Path.GetTempPath(), $"jwt-other-{Guid.NewGuid():N}");
        try
        {
            var otherService = new JwtTokenService(otherDir);
            var (token, _) = otherService.GenerateAccessToken(MakeUser());

            var result = _sut.ValidateToken(token);

            result.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(otherDir))
                Directory.Delete(otherDir, recursive: true);
        }
    }

    [Fact]
    public void GenerateAccessToken_ShouldPersistKeyToDisk()
    {
        // Key is persisted during construction
        var keyPath = Path.Combine(_tempDir, "jwt-signing-key.xml");

        File.Exists(keyPath).Should().BeTrue();
        File.ReadAllText(keyPath).Should().Contain("<RSAKeyValue>");
    }

    [Fact]
    public void Constructor_ShouldReuseExistingKey_WhenKeyFileExists()
    {
        // _sut already created the key file — create a second instance from the same dir
        var secondService = new JwtTokenService(_tempDir);

        // Token from first service should be valid with second service
        var (token, _) = _sut.GenerateAccessToken(MakeUser());
        var principal = secondService.ValidateToken(token);

        principal.Should().NotBeNull();
    }
}
