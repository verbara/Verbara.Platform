using Asterisk.Platform.Api.Services;
using OtpNet;

namespace Asterisk.Platform.Api.Tests.Services;

public sealed class MfaServiceTests
{
    private readonly MfaService _sut = new();

    [Fact]
    public void GenerateSetup_ShouldReturnSecretAndQrUri()
    {
        var (secret, qrUri) = _sut.GenerateSetup("user@test.com");

        secret.Should().NotBeNullOrWhiteSpace();
        qrUri.Should().Contain("otpauth://totp/");
        qrUri.Should().Contain("user%40test.com");
        qrUri.Should().Contain($"secret={secret}");
    }

    [Fact]
    public void VerifyCode_ShouldReturnTrue_WhenCodeIsValid()
    {
        var (secret, _) = _sut.GenerateSetup("user@test.com");

        // Generate the current valid code using the same secret
        var secretBytes = Base32Encoding.ToBytes(secret);
        var totp = new Totp(secretBytes);
        var code = totp.ComputeTotp();

        _sut.VerifyCode(secret, code).Should().BeTrue();
    }

    [Fact]
    public void VerifyCode_ShouldReturnFalse_WhenCodeIsInvalid()
    {
        var (secret, _) = _sut.GenerateSetup("user@test.com");

        _sut.VerifyCode(secret, "000000").Should().BeFalse();
    }

    [Fact]
    public void GenerateRecoveryCodes_ShouldReturn10UniqueCodes()
    {
        var codes = _sut.GenerateRecoveryCodes();

        codes.Should().HaveCount(10);
        codes.Distinct().Should().HaveCount(10);
        codes.Should().AllSatisfy(c => c.Length.Should().Be(16)); // 8 bytes = 16 hex chars
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatches()
    {
        var codes = _sut.GenerateRecoveryCodes();
        var hashed = _sut.HashRecoveryCodes(codes);

        var (isValid, index) = _sut.ValidateRecoveryCode(codes[3], hashed);

        isValid.Should().BeTrue();
        index.Should().Be(3);
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnFalse_WhenCodeDoesNotMatch()
    {
        var codes = _sut.GenerateRecoveryCodes();
        var hashed = _sut.HashRecoveryCodes(codes);

        var (isValid, index) = _sut.ValidateRecoveryCode("not-a-valid-code", hashed);

        isValid.Should().BeFalse();
        index.Should().Be(-1);
    }
}
