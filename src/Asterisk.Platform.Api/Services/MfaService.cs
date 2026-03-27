using System.Security.Cryptography;
using OtpNet;

namespace Asterisk.Platform.Api.Services;

internal sealed class MfaService
{
    private const int SecretSize = 20;
    private const int RecoveryCodeCount = 10;
    private const int RecoveryCodeBytes = 8;

    public (string Secret, string QrUri) GenerateSetup(string email, string issuer = "AsteriskPlatform")
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(SecretSize);
        var secret = Base32Encoding.ToString(secretBytes);
        var qrUri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}" +
                    $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
        return (secret, qrUri);
    }

    public bool VerifyCode(string secret, string code)
    {
        var secretBytes = Base32Encoding.ToBytes(secret);
        var totp = new Totp(secretBytes);
        return totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
    }

    public IReadOnlyList<string> GenerateRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(RecoveryCodeBytes);
            codes.Add(Convert.ToHexStringLower(bytes));
        }
        return codes;
    }

    public IReadOnlyList<string> HashRecoveryCodes(IReadOnlyList<string> codes) =>
        codes.Select(c => BCrypt.Net.BCrypt.HashPassword(c, workFactor: 10)).ToList();

    public (bool IsValid, int Index) ValidateRecoveryCode(string code, IReadOnlyList<string> hashedCodes)
    {
        for (var i = 0; i < hashedCodes.Count; i++)
        {
            if (BCrypt.Net.BCrypt.Verify(code, hashedCodes[i]))
                return (true, i);
        }
        return (false, -1);
    }
}
