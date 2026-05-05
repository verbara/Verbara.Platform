using System.IdentityModel.Tokens.Jwt;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// AHH Phase 3.B — exercises the rotation-pool constructor of
/// <see cref="JwtTokenService"/> alongside the
/// <see cref="JwtLegacyKeyMigrationService"/> startup hook. The existing
/// <see cref="JwtTokenServiceTests"/> covers the file-based path
/// unchanged; these tests cover the new pool-based path end-to-end:
/// HS256 issuance + verify, RS256 issuance + verify, multi-key validation
/// across rotations, and the legacy-file → RS256 import.
/// </summary>
public sealed class JwtTokenServiceRotationTests
{
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

    private static JwtKeyRotationService NewRotationService(IJwtKeyStore? store = null)
    {
        store ??= new InMemoryJwtKeyStore();
        return new JwtKeyRotationService(
            store,
            TimeProvider.System,
            Options.Create(new JwtKeyRotationOptions
            {
                KeySizeBytes = 32,
                ActiveDuration = TimeSpan.FromHours(1),
                GracePeriod = TimeSpan.FromHours(2),
            }));
    }

    [Fact]
    public async Task GenerateAccessToken_ShouldIssueAndValidate_WhenPoolHasHs256Key()
    {
        using var rotation = NewRotationService();
        // Pre-rotate so the pool has at least one active HS256 entry.
        _ = await rotation.RotateAsync();

        var sut = new JwtTokenService(rotation, new InMemoryJtiRevocationCache());

        var (token, _) = sut.GenerateAccessToken(MakeUser());
        var principal = await sut.ValidateTokenAsync(token, CancellationToken.None);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtRegisteredClaimNames.Sub)?.Value.Should().Be("user1");
    }

    [Fact]
    public async Task GenerateAccessToken_ShouldEmitKidHeader_WhenPoolEntryHasKeyId()
    {
        using var rotation = NewRotationService();
        var entry = await rotation.RotateAsync();

        var sut = new JwtTokenService(rotation, new InMemoryJtiRevocationCache());

        var (token, _) = sut.GenerateAccessToken(MakeUser());

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Header.Kid.Should().Be(entry.KeyId);
    }

    [Fact]
    public async Task ValidateToken_ShouldAcceptToken_WhenSignedWithGracePeriodKeyAfterRotation()
    {
        // Bootstrap pool with first key, sign a token with it, then add a
        // second key (rotation). The first token must still validate against
        // the grace-window entry.
        var store = new InMemoryJwtKeyStore();
        using var rotation = NewRotationService(store);

        var firstKey = await rotation.RotateAsync();
        var sut = new JwtTokenService(rotation, new InMemoryJtiRevocationCache());

        var (firstToken, _) = sut.GenerateAccessToken(MakeUser());

        // Force cache invalidation by rotating; the new key takes over signing.
        _ = await rotation.RotateAsync();

        // Issue a fresh JwtTokenService to bypass the 60 s active-key cache so
        // the next sign uses the second key.
        var sutAfterRotation = new JwtTokenService(rotation, new InMemoryJtiRevocationCache());
        var (secondToken, _) = sutAfterRotation.GenerateAccessToken(MakeUser());

        // The original token (signed by firstKey) MUST still validate because
        // firstKey is in the grace window.
        var firstPrincipal = await sutAfterRotation.ValidateTokenAsync(firstToken, CancellationToken.None);
        firstPrincipal.Should().NotBeNull();

        // And the new token validates too.
        var secondPrincipal = await sutAfterRotation.ValidateTokenAsync(secondToken, CancellationToken.None);
        secondPrincipal.Should().NotBeNull();

        // The two tokens use different kids.
        var firstJwt = new JwtSecurityTokenHandler().ReadJwtToken(firstToken);
        var secondJwt = new JwtSecurityTokenHandler().ReadJwtToken(secondToken);
        firstJwt.Header.Kid.Should().Be(firstKey.KeyId);
        secondJwt.Header.Kid.Should().NotBe(firstKey.KeyId);
    }

    [Fact]
    public async Task GenerateAccessToken_ShouldUseRsa_WhenPoolHasRs256ActiveEntry()
    {
        // Build an RS256 entry directly in the store (simulating the legacy
        // migration path) and verify both issuance + validation work.
        var store = new InMemoryJwtKeyStore();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var now = DateTimeOffset.UtcNow;

        var entry = new JwtKeyEntry
        {
            KeyId = "rsa-test-kid",
            Key = Convert.ToBase64String(pkcs8),
            Algorithm = JwtKeyAlgorithm.Rs256,
            ActivatedAt = now,
            ExpiresAt = now.AddDays(30),
            IsActive = true,
        };
        await store.UpsertAsync(entry);

        using var rotation = NewRotationService(store);
        var sut = new JwtTokenService(rotation, new InMemoryJtiRevocationCache());

        var (token, _) = sut.GenerateAccessToken(MakeUser());
        var principal = await sut.ValidateTokenAsync(token, CancellationToken.None);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtRegisteredClaimNames.Sub)?.Value.Should().Be("user1");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Header.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
        jwt.Header.Kid.Should().Be("rsa-test-kid");
    }

    [Fact]
    public async Task GenerateImpersonationToken_ShouldIssueAndCarryClaims_WhenPoolHasActiveKey()
    {
        using var rotation = NewRotationService();
        _ = await rotation.RotateAsync();

        var sut = new JwtTokenService(rotation, new InMemoryJtiRevocationCache());

        var admin = MakeUser();
        var permissions = new HashSet<string>(["platform:tenant:impersonate"], StringComparer.Ordinal);
        var (token, _) = sut.GenerateImpersonationToken(admin, "target-tenant", permissions, readOnly: true);
        var principal = await sut.ValidateTokenAsync(token, CancellationToken.None);

        principal.Should().NotBeNull();
        principal!.FindFirst("tid")?.Value.Should().Be("target-tenant");
        principal.FindFirst("impersonation")?.Value.Should().Be("true");
        principal.FindFirst("readonly")?.Value.Should().Be("true");
    }

    [Fact]
    public async Task ValidateToken_ShouldRejectToken_WhenSigningKeyIsNotInPool()
    {
        // Sign an HS256 token with an OUTSIDE key, then validate against a
        // pool that has a different key. Must be rejected.
        using var pool = NewRotationService();
        _ = await pool.RotateAsync();

        // Build a totally separate signing pipeline using a different key.
        var randomBytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(randomBytes);
        var foreignKey = new SymmetricSecurityKey(randomBytes) { KeyId = "foreign-kid" };
        var foreignCreds = new SigningCredentials(foreignKey, SecurityAlgorithms.HmacSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sub, "evil-user")]),
            Expires = DateTime.UtcNow.AddMinutes(15),
            Issuer = "verbara-platform",
            Audience = "verbara-platform",
            SigningCredentials = foreignCreds,
        };
        var foreignToken = new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);

        var sut = new JwtTokenService(pool, new InMemoryJtiRevocationCache());
        var principal = await sut.ValidateTokenAsync(foreignToken, CancellationToken.None);

        principal.Should().BeNull();
    }

    [Fact]
    public async Task LegacyMigrationService_ShouldImportFile_WhenPoolIsEmptyAndFileExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jwt-mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Seed a legacy file using the file-based JwtTokenService — this
            // is exactly the artifact the migration shim should pick up.
            var dp = DataProtectionProvider.Create("Verbara.Platform.Tests");
            var fileSeeded = new JwtTokenService(tempDir, dp, new InMemoryJtiRevocationCache());
            _ = fileSeeded.GenerateAccessToken(MakeUser()); // sanity
            File.Exists(Path.Combine(tempDir, "jwt-signing-key.xml")).Should().BeTrue();

            var store = new InMemoryJwtKeyStore();
            var migration = new JwtLegacyKeyMigrationService(
                store,
                dp,
                tempDir,
                NullLogger<JwtLegacyKeyMigrationService>.Instance);

            await migration.StartAsync(CancellationToken.None);

            var active = await store.GetActiveAsync();
            active.Should().NotBeNull();
            active!.Algorithm.Should().Be(JwtKeyAlgorithm.Rs256);
            active.IsActive.Should().BeTrue();
            active.KeyId.Should().StartWith("platform-jwt-");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyMigrationService_ShouldBeNoOp_WhenPoolAlreadyHasActiveKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jwt-mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dp = DataProtectionProvider.Create("Verbara.Platform.Tests");

            var store = new InMemoryJwtKeyStore();
            using var rotation = NewRotationService(store);
            var pre = await rotation.RotateAsync(); // pool now has HS256 active

            // Seed a legacy file too — the migration should ignore it.
            var fileSeeded = new JwtTokenService(tempDir, dp, new InMemoryJtiRevocationCache());
            _ = fileSeeded.GenerateAccessToken(MakeUser());

            var migration = new JwtLegacyKeyMigrationService(
                store,
                dp,
                tempDir,
                NullLogger<JwtLegacyKeyMigrationService>.Instance);
            await migration.StartAsync(CancellationToken.None);

            var active = await store.GetActiveAsync();
            active.Should().NotBeNull();
            active!.KeyId.Should().Be(pre.KeyId);
            active.Algorithm.Should().Be(JwtKeyAlgorithm.Hs256);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyMigrationService_ShouldBeNoOp_WhenLegacyFileMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jwt-mig-{Guid.NewGuid():N}");
        try
        {
            // Note: NOT creating tempDir → file definitely missing.
            var dp = DataProtectionProvider.Create("Verbara.Platform.Tests");
            var store = new InMemoryJwtKeyStore();

            var migration = new JwtLegacyKeyMigrationService(
                store,
                dp,
                tempDir,
                NullLogger<JwtLegacyKeyMigrationService>.Instance);
            await migration.StartAsync(CancellationToken.None);

            (await store.GetActiveAsync()).Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
