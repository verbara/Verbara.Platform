namespace Verbara.Platform.Core.Email;

public sealed record BrandingContext(
    string CompanyName,
    string? LogoUrl,
    string PrimaryColor,
    string SecondaryColor,
    string AccentColor,
    string? SupportEmail,
    string? SupportUrl,
    string FromName,
    string FromAddress);
