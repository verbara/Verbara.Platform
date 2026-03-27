using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class PasswordService
{
    private const int WorkFactor = 12;

    public string HashPassword(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, workFactor: WorkFactor);

    public bool VerifyPassword(string password, string hash) =>
        BCrypt.Net.BCrypt.Verify(password, hash);

    public PasswordValidationResult ValidatePolicy(string password, TenantAuthConfig config)
    {
        var errors = new List<string>();

        if (password.Length < config.PasswordMinLength)
            errors.Add($"Password must be at least {config.PasswordMinLength} characters");

        if (config.PasswordRequireUppercase && !password.Any(char.IsUpper))
            errors.Add("Password must contain at least one uppercase letter");

        if (config.PasswordRequireNumber && !password.Any(char.IsDigit))
            errors.Add("Password must contain at least one number");

        if (config.PasswordRequireSpecial && password.All(char.IsLetterOrDigit))
            errors.Add("Password must contain at least one special character");

        return new PasswordValidationResult(errors.Count == 0, errors);
    }
}

internal sealed record PasswordValidationResult(bool IsValid, IReadOnlyList<string> Errors);
