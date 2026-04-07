namespace Asterisk.Platform.Core.Branding;

public sealed class TenantBranding
{
    public required string TenantId { get; init; }
    public string? DisplayName { get; set; }
    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }
    public string? AccentColor { get; set; }
    public string? Locale { get; set; }
    public string? Timezone { get; set; }
    public string? Subdomain { get; set; }
    public string? SupportEmail { get; set; }
    public string? SupportUrl { get; set; }
    public string? EmailFromName { get; set; }
    public string? EmailFromAddress { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
