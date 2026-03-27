using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Tests.Services;

public sealed class PasswordServiceTests
{
    private readonly PasswordService _sut = new();

    private static TenantAuthConfig DefaultConfig() => new()
    {
        TenantId = "t1",
        PasswordMinLength = 12,
        PasswordRequireUppercase = true,
        PasswordRequireNumber = true,
        PasswordRequireSpecial = true,
    };

    [Fact]
    public void HashPassword_ShouldReturnBCryptHash()
    {
        var hash = _sut.HashPassword("SuperSecret1!");

        hash.Should().StartWith("$2");
        hash.Length.Should().BeGreaterThan(50);
    }

    [Fact]
    public void VerifyPassword_ShouldReturnTrue_WhenPasswordMatches()
    {
        var password = "SuperSecret1!";
        var hash = _sut.HashPassword(password);

        _sut.VerifyPassword(password, hash).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_ShouldReturnFalse_WhenPasswordDoesNotMatch()
    {
        var hash = _sut.HashPassword("SuperSecret1!");

        _sut.VerifyPassword("WrongPassword1!", hash).Should().BeFalse();
    }

    [Fact]
    public void ValidatePolicy_ShouldPass_WhenPasswordMeetsAllRequirements()
    {
        var result = _sut.ValidatePolicy("SuperSecret1!@", DefaultConfig());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidatePolicy_ShouldFail_WhenPasswordTooShort()
    {
        var result = _sut.ValidatePolicy("Short1!", DefaultConfig());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("at least 12 characters"));
    }

    [Fact]
    public void ValidatePolicy_ShouldFail_WhenMissingUppercase()
    {
        var result = _sut.ValidatePolicy("supersecret1!@", DefaultConfig());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("uppercase"));
    }

    [Fact]
    public void ValidatePolicy_ShouldFail_WhenMissingNumber()
    {
        var result = _sut.ValidatePolicy("SuperSecretAB!", DefaultConfig());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("number"));
    }

    [Fact]
    public void ValidatePolicy_ShouldFail_WhenMissingSpecialCharacter()
    {
        var result = _sut.ValidatePolicy("SuperSecret12A", DefaultConfig());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("special character"));
    }
}
