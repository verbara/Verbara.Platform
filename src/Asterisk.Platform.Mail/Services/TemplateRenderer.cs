using System.Collections.Concurrent;
using System.Reflection;
using Asterisk.Platform.Core.Email;

namespace Asterisk.Platform.Mail.Services;

internal sealed class TemplateRenderer : IEmailTemplateService
{
    private static readonly Assembly _assembly = typeof(TemplateRenderer).Assembly;
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public string Render(string templateName, BrandingContext branding,
                         IReadOnlyDictionary<string, string> variables)
    {
        var content = LoadTemplate(templateName);
        if (content is null)
            return $"<p>Template '{templateName}' not found.</p>";

        var layout = LoadTemplate("_base-layout");
        if (layout is null)
            return $"<p>Base layout template not found.</p>";

        var rendered = layout.Replace("{{Content}}", content, StringComparison.Ordinal);
        rendered = ApplyBranding(rendered, branding);
        foreach (var (key, value) in variables)
            rendered = rendered.Replace($"{{{{{key}}}}}", EscapeHtml(value), StringComparison.Ordinal);
        return rendered;
    }

    private static string? LoadTemplate(string templateName)
    {
        return _cache.GetOrAdd(templateName, name =>
        {
            var resourceName = $"Asterisk.Platform.Mail.Templates.{name}.html";
            using var stream = _assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }) is { Length: > 0 } cached ? cached : null;
    }

    private static string ApplyBranding(string template, BrandingContext branding)
    {
        template = template.Replace("{{CompanyName}}", EscapeHtml(branding.CompanyName), StringComparison.Ordinal);
        template = template.Replace("{{PrimaryColor}}", EscapeHtml(branding.PrimaryColor), StringComparison.Ordinal);
        template = template.Replace("{{SecondaryColor}}", EscapeHtml(branding.SecondaryColor), StringComparison.Ordinal);
        template = template.Replace("{{AccentColor}}", EscapeHtml(branding.AccentColor), StringComparison.Ordinal);
        template = template.Replace("{{LogoUrl}}", branding.LogoUrl is not null ? EscapeHtml(branding.LogoUrl) : string.Empty, StringComparison.Ordinal);
        template = template.Replace("{{SupportEmail}}", branding.SupportEmail is not null ? EscapeHtml(branding.SupportEmail) : string.Empty, StringComparison.Ordinal);
        template = template.Replace("{{SupportUrl}}", branding.SupportUrl is not null ? EscapeHtml(branding.SupportUrl) : string.Empty, StringComparison.Ordinal);
        return template;
    }

    private static string EscapeHtml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal);
}
