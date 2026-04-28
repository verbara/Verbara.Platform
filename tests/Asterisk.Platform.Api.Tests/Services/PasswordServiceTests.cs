using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Tests.Services;

public sealed class PasswordServiceTests
{
    private static TenantAuthConfig DefaultConfig() => new()
    {
        TenantId = "t1",
        PasswordMinLength = 12,
        PasswordRequireUppercase = true,
        PasswordRequireNumber = true,
        PasswordRequireSpecial = true,
    };

    [Fact]
    public void HashPassword_ShouldEmitArgon2id_AfterPhase4Migration()
    {
        var hash = PasswordService.HashPassword("SuperSecret1!");

        hash.Should().StartWith("$argon2id$",
            because: "AHH Phase 4 makes Argon2id the canonical hash; new passwords never go to BCrypt");
        hash.Length.Should().BeGreaterThan(50);
    }

    [Fact]
    public void VerifyPassword_ShouldReturnTrue_WhenArgon2idHashMatches()
    {
        var password = "SuperSecret1!";
        var hash = PasswordService.HashPassword(password);

        PasswordService.VerifyPassword(password, hash).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_ShouldReturnFalse_WhenArgon2idHashDoesNotMatch()
    {
        var hash = PasswordService.HashPassword("SuperSecret1!");

        PasswordService.VerifyPassword("WrongPassword1!", hash).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_ShouldReturnTrue_WhenLegacyBcryptHashMatches()
    {
        // AHH Phase 4 — backward-compat regression: pre-migration BCrypt
        // hashes (cost=12) MUST still verify so already-deployed users
        // can log in until the on-login rehash migrates them.
        const string password = "SuperSecret1!";
        var legacyBcrypt = BCrypt.Net.BCrypt.HashPassword(password, workFactor: PasswordService.LegacyBcryptWorkFactor);

        legacyBcrypt.Should().StartWith("$2");
        PasswordService.VerifyPassword(password, legacyBcrypt).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_ShouldReturnFalse_WhenLegacyBcryptHashDoesNotMatch()
    {
        var legacyBcrypt = BCrypt.Net.BCrypt.HashPassword("Original1!", workFactor: PasswordService.LegacyBcryptWorkFactor);

        PasswordService.VerifyPassword("WrongOriginal1!", legacyBcrypt).Should().BeFalse();
    }

    [Fact]
    public void IsBcryptHash_ShouldReturnTrue_ForLegacyBcryptHash()
    {
        var bcryptHash = BCrypt.Net.BCrypt.HashPassword("SuperSecret1!", workFactor: PasswordService.LegacyBcryptWorkFactor);

        PasswordService.IsBcryptHash(bcryptHash).Should().BeTrue();
    }

    [Fact]
    public void IsBcryptHash_ShouldReturnFalse_ForArgon2idHash()
    {
        var argon2idHash = PasswordService.HashPassword("SuperSecret1!");

        PasswordService.IsBcryptHash(argon2idHash).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_ShouldReturnFalse_WhenBcryptHashIsMalformed()
    {
        // Defensive: a corrupt BCrypt header should NOT throw — the verify
        // path treats it as a non-match so the failure path doesn't reveal
        // hash shape via exception type.
        PasswordService.VerifyPassword("anything", "$2a$malformed").Should().BeFalse();
    }

    [Fact]
    public void ValidatePolicy_ShouldPass_WhenPasswordMeetsAllRequirements()
    {
        var result = PasswordService.ValidatePolicy("SuperSecret1!@", DefaultConfig());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidatePolicy_ShouldFail_WhenPasswordTooShort()
    {
        var result = PasswordService.ValidatePolicy("Short1!", DefaultConfig());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("at least 12 characters"));
    }

    [Fact]
    public void ValidatePolicy_ShouldFail_WhenMissingUppercase()
    {
        var result = PasswordService.ValidatePolicy("supersecret1!@", DefaultConfig());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("uppercase"));
    }

    [Fact]
    public void ValidatePolicy_ShouldFail_WhenMissingNumber()
    {
        var result = PasswordService.ValidatePolicy("SuperSecretAB!", DefaultConfig());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("number"));
    }

    [Fact]
    public void ValidatePolicy_ShouldFail_WhenMissingSpecialCharacter()
    {
        var result = PasswordService.ValidatePolicy("SuperSecret12A", DefaultConfig());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("special character"));
    }
}
