using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Asterisk.Platform.Core.Email;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class HttpEmailTemplateService(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpEmailTemplateService> logger) : IEmailTemplateService
{
    public string Render(string templateName, BrandingContext branding,
                         IReadOnlyDictionary<string, string> variables)
    {
        // IEmailTemplateService.Render is synchronous — use sync-over-async for the HTTP call.
        // This is acceptable because template rendering is only called from background services
        // (ReportSchedulerService, NotificationService, AuthEndpoints) — never from the request hot path.
        return RenderAsync(templateName, branding, variables).GetAwaiter().GetResult();
    }

    private async Task<string> RenderAsync(string templateName, BrandingContext branding,
                                           IReadOnlyDictionary<string, string> variables)
    {
        using var client = httpClientFactory.CreateClient("mail");

        var request = new RenderTemplateRequest(templateName, branding,
            new Dictionary<string, string>(variables));

        using var response = await client.PostAsJsonAsync(
            "/api/v1/render-template", request,
            TemplateJsonContext.Default.RenderTemplateRequest).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            LogRenderFailed((int)response.StatusCode, templateName);
            return $"<p>Template '{templateName}' rendering failed.</p>";
        }

        var result = await response.Content.ReadFromJsonAsync(
            TemplateJsonContext.Default.RenderTemplateResponse).ConfigureAwait(false);

        return result?.Html ?? $"<p>Template '{templateName}' not found.</p>";
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Template render via mail service failed with status {StatusCode} for template={TemplateName}")]
    private partial void LogRenderFailed(int statusCode, string templateName);
}

internal sealed record RenderTemplateRequest(
    string TemplateName,
    BrandingContext Branding,
    Dictionary<string, string> Variables);

internal sealed record RenderTemplateResponse(string Html);

[JsonSerializable(typeof(RenderTemplateRequest))]
[JsonSerializable(typeof(RenderTemplateResponse))]
[JsonSerializable(typeof(BrandingContext))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class TemplateJsonContext : JsonSerializerContext;
