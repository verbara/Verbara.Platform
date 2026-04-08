namespace Asterisk.Platform.Core.Email;

public interface IEmailTemplateService
{
    string Render(string templateName, BrandingContext branding,
                  IReadOnlyDictionary<string, string> variables);
}
