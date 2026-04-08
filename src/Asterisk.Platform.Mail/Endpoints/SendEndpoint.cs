using Asterisk.Platform.Core.Email;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Mail.Endpoints;

internal static class SendEndpoint
{
    public static RouteGroupBuilder MapSendEndpoint(this RouteGroupBuilder group)
    {
        group.MapPost("/send", async (
            EmailMessage message,
            [FromServices] IEmailService emailService,
            CancellationToken ct) =>
        {
            await emailService.SendAsync(message, ct);
            return Results.Accepted();
        })
        .WithName("SendEmail")
        .WithTags("Email");

        return group;
    }
}
