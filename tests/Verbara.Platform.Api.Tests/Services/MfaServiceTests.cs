using Verbara.Platform.Api.Services;
using OtpNet;

namespace Verbara.Platform.Api.Tests.Services;

public sealed class MfaServiceTests
{
    [Fact]
    public void GenerateSetup_ShouldReturnSecretAndQrUri()
    {
        var (secret, qrUri) = MfaService.GenerateSetup("user@test.com");

        secret.Should().NotBeNullOrWhiteSpace();
        qrUri.Should().Contain("otpauth://totp/");
        qrUri.Should().Contain("user%40test.com");
        qrUri.Should().Contain($"secret={secret}");
    }

    [Fact]
    public void VerifyCode_ShouldReturnTrue_WhenCodeIsValid()
    {
        var (secret, _) = MfaService.GenerateSetup("user@test.com");

        // Generate the current valid code using the same secret
        var secretBytes = Base32Encoding.ToBytes(secret);
        var totp = new Totp(secretBytes);
        var code = totp.ComputeTotp();

        MfaService.VerifyCode(secret, code).Should().BeTrue();
    }

    [Fact]
    public void VerifyCode_ShouldReturnFalse_WhenCodeIsInvalid()
    {
        var (secret, _) = MfaService.GenerateSetup("user@test.com");

        MfaService.VerifyCode(secret, "000000").Should().BeFalse();
    }

    [Fact]
    public void GenerateRecoveryCodes_ShouldReturn10UniqueCodes()
    {
        var codes = MfaService.GenerateRecoveryCodes();

        codes.Should().HaveCount(10);
        codes.Distinct().Should().HaveCount(10);
        codes.Should().AllSatisfy(c => c.Length.Should().Be(16)); // 8 bytes = 16 hex chars
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatches()
    {
        var codes = MfaService.GenerateRecoveryCodes();
        var hashed = MfaService.HashRecoveryCodes(codes);

        var (isValid, index) = MfaService.ValidateRecoveryCode(codes[3], hashed);

        isValid.Should().BeTrue();
        index.Should().Be(3);
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnFalse_WhenCodeDoesNotMatch()
    {
        var codes = MfaService.GenerateRecoveryCodes();
        var hashed = MfaService.HashRecoveryCodes(codes);

        var (isValid, index) = MfaService.ValidateRecoveryCode("not-a-valid-code", hashed);

        isValid.Should().BeFalse();
        index.Should().Be(-1);
    }
}
