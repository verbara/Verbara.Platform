using Verbara.Platform.Core.Email;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Mail.Endpoints;

internal static class TemplateEndpoint
{
    public static RouteGroupBuilder MapTemplateEndpoint(this RouteGroupBuilder group)
    {
        group.MapPost("/render-template", (
            RenderTemplateRequest request,
            [FromServices] IEmailTemplateService templateService) =>
        {
            var html = templateService.Render(request.TemplateName, request.Branding, request.Variables);
            return Results.Ok(new RenderTemplateResponse(html));
        })
        .WithName("RenderTemplate")
        .WithTags("Email");

        return group;
    }
}

internal sealed record RenderTemplateRequest(
    string TemplateName,
    BrandingContext Branding,
    Dictionary<string, string> Variables);

internal sealed record RenderTemplateResponse(string Html);
