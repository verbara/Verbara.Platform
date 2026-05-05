using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Api.Tests.Services;

public sealed class JwtTokenServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly InMemoryJtiRevocationCache _cache;
    private readonly JwtTokenService _sut;

    public JwtTokenServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jwt-test-{Guid.NewGuid():N}");
        _dataProtection = DataProtectionProvider.Create("Verbara.Platform.Tests");
        _cache = new InMemoryJtiRevocationCache();
        _sut = new JwtTokenService(_tempDir, _dataProtection, _cache);
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

    // ── Existing tests (updated for new constructor) ─────────────────────────

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
    public async Task ValidateTokenAsync_ShouldReturnPrincipal_WhenTokenIsValid()
    {
        var (token, _) = _sut.GenerateAccessToken(MakeUser());

        var principal = await _sut.ValidateTokenAsync(token, CancellationToken.None);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtRegisteredClaimNames.Sub)?.Value.Should().Be("user1");
    }

    [Fact]
    public async Task ValidateTokenAsync_ShouldReturnNull_WhenTokenIsInvalid()
    {
        var result = await _sut.ValidateTokenAsync("not-a-valid-jwt", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_ShouldReturnNull_WhenTokenSignedByDifferentKey()
    {
        var otherDir = Path.Combine(Path.GetTempPath(), $"jwt-other-{Guid.NewGuid():N}");
        try
        {
            var otherService = new JwtTokenService(otherDir, _dataProtection, new InMemoryJtiRevocationCache());
            var (token, _) = otherService.GenerateAccessToken(MakeUser());

            var result = await _sut.ValidateTokenAsync(token, CancellationToken.None);

            result.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(otherDir))
                Directory.Delete(otherDir, recursive: true);
        }
    }

    [Fact]
    public async Task Constructor_ShouldReuseExistingKey_WhenKeyFileExists()
    {
        var secondService = new JwtTokenService(_tempDir, _dataProtection, new InMemoryJtiRevocationCache());

        var (token, _) = _sut.GenerateAccessToken(MakeUser());
        var principal = await secondService.ValidateTokenAsync(token, CancellationToken.None);

        principal.Should().NotBeNull();
    }

    // ── New B.1 tests — jti claim ─────────────────────────────────────────────

    [Fact]
    public void GenerateAccessToken_ShouldIncludeJtiClaim_WhenCalled()
    {
        var (token, _) = _sut.GenerateAccessToken(MakeUser());

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        var jti = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti);
        jti.Should().NotBeNull();
        jti!.Value.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateImpersonationToken_ShouldIncludeJtiClaim_WhenCalled()
    {
        var permissions = new HashSet<string> { "read:agents" };
        var (token, _) = _sut.GenerateImpersonationToken(MakeUser(), "t2", permissions);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        var jti = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti);
        jti.Should().NotBeNull();
        jti!.Value.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateAccessToken_ShouldIncludeUniqueJti_WhenCalledTwice()
    {
        var handler = new JwtSecurityTokenHandler();
        var user = MakeUser();

        var (token1, _) = _sut.GenerateAccessToken(user);
        var (token2, _) = _sut.GenerateAccessToken(user);

        var jti1 = handler.ReadJwtToken(token1).Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        var jti2 = handler.ReadJwtToken(token2).Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;

        jti1.Should().NotBe(jti2);
    }

    // ── New B.4 test — fingerprint-based kid ─────────────────────────────────

    [Fact]
    public void KeyId_ShouldBeFingerprintBased_WhenGenerated()
    {
        _sut.KeyId.Should().MatchRegex(@"^platform-jwt-[0-9a-f]{16}$");
    }

    // ── New B.5 test — jti revocation ─────────────────────────────────────────

    [Fact]
    public async Task ValidateTokenAsync_ShouldReturnNull_WhenJtiRevoked()
    {
        var (token, expiresAt) = _sut.GenerateAccessToken(MakeUser());

        // Extract jti from the issued token
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        var jti = jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;

        // Revoke the jti before validation
        await _cache.RevokeAsync(jti, expiresAt, CancellationToken.None);

        var principal = await _sut.ValidateTokenAsync(token, CancellationToken.None);

        principal.Should().BeNull();
    }

    // ── New B.3 test — DataProtection migration of legacy plaintext key ───────

    [Fact]
    public void LoadsSigningKey_ShouldMigrateLegacyPlaintext_WhenFileIsPlaintextXml()
    {
        // Arrange: write a plaintext XML key file (simulating pre-v1.9.2 deployment)
        var legacyDir = Path.Combine(Path.GetTempPath(), $"jwt-legacy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(legacyDir);
            var keyPath = Path.Combine(legacyDir, "jwt-signing-key.xml");
            using var rsa = RSA.Create(2048);
            var plaintextXml = rsa.ToXmlString(includePrivateParameters: true);
            File.WriteAllText(keyPath, plaintextXml);

            // Act: construct service with the legacy plaintext file
            _ = new JwtTokenService(legacyDir, _dataProtection, new InMemoryJtiRevocationCache());

            // Assert: the file is now encrypted (raw bytes are NOT valid XML)
            var rawBytes = File.ReadAllBytes(keyPath);
            var rawString = Encoding.UTF8.GetString(rawBytes);
            var isStillPlaintext = rawString.Contains("<RSAKeyValue>");

            isStillPlaintext.Should().BeFalse("the plaintext key should have been migrated to encrypted format");
        }
        finally
        {
            if (Directory.Exists(legacyDir))
                Directory.Delete(legacyDir, recursive: true);
        }
    }

    // ── New test for GenerateAccessToken_ShouldPersistKeyToDisk ──────────────
    // (original test expected plaintext XML; now file is encrypted binary)

    [Fact]
    public void GenerateAccessToken_ShouldPersistKeyToDisk()
    {
        var keyPath = Path.Combine(_tempDir, "jwt-signing-key.xml");

        File.Exists(keyPath).Should().BeTrue();
        // File is now encrypted — raw bytes should NOT be parseable as XML
        var raw = File.ReadAllBytes(keyPath);
        Encoding.UTF8.GetString(raw).Should().NotContain("<RSAKeyValue>",
            "the persisted key must be encrypted, not stored as plaintext XML");
    }
}
