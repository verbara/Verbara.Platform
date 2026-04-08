using Asterisk.Platform.Api.Services.Email;
using Asterisk.Platform.Core.Email;

namespace Asterisk.Platform.Api.Tests.Services;

public sealed class EmailTemplateServiceTests
{
    private static BrandingContext DefaultBranding() => new(
        CompanyName: "Acme Corp",
        LogoUrl: "https://example.com/logo.png",
        PrimaryColor: "#1976d2",
        SecondaryColor: "#424242",
        AccentColor: "#ff9800",
        SupportEmail: "support@acme.com",
        SupportUrl: "https://help.acme.com",
        FromName: "Acme Corp",
        FromAddress: "no-reply@acme.com");

    private static EmbeddedEmailTemplateService CreateService() => new();

    [Fact]
    public void Render_ShouldInjectBranding_WhenTemplateExists()
    {
        var service = CreateService();
        var branding = DefaultBranding();
        var variables = new Dictionary<string, string>
        {
            ["Title"] = "Critical Alert",
            ["Body"] = "Something went wrong.",
            ["ActionUrl"] = "https://example.com/action"
        };

        var result = service.Render("notification-critical", branding, variables);

        result.Should().Contain("Acme Corp");
        result.Should().Contain("#1976d2");
        result.Should().Contain("Critical Alert");
    }

    [Fact]
    public void Render_ShouldIncludeBaseLayout_WhenRendered()
    {
        var service = CreateService();
        var branding = DefaultBranding();
        var variables = new Dictionary<string, string>
        {
            ["Title"] = "Warning",
            ["Body"] = "Check your quota.",
            ["ActionUrl"] = "https://example.com/warning"
        };

        var result = service.Render("notification-warning", branding, variables);

        result.Should().StartWith("<!DOCTYPE html>");
        result.Should().Contain("support@acme.com");
        result.Should().Contain("Check your quota.");
    }

    [Fact]
    public void Render_ShouldReturnFallback_WhenTemplateNotFound()
    {
        var service = CreateService();
        var branding = DefaultBranding();
        var variables = new Dictionary<string, string>();

        var result = service.Render("nonexistent-template", branding, variables);

        result.Should().Contain("not found");
    }

    [Fact]
    public void Render_ShouldEscapeHtml_WhenVariablesContainSpecialChars()
    {
        var service = CreateService();
        var branding = DefaultBranding();
        var variables = new Dictionary<string, string>
        {
            ["Title"] = "<script>alert('xss')</script>",
            ["Body"] = "All good & fine.",
            ["ActionUrl"] = "https://example.com"
        };

        var result = service.Render("notification-critical", branding, variables);

        result.Should().Contain("&lt;script&gt;");
        result.Should().NotContain("<script>");
    }
}
