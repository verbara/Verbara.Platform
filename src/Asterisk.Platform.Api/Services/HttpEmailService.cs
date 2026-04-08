using System.Net.Http.Json;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Core.Email;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class HttpEmailService(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpEmailService> logger) : IEmailService
{
    public async ValueTask SendAsync(EmailMessage message, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("mail");
        using var response = await client.PostAsJsonAsync(
            "/api/v1/send", message, ApiJsonContext.Default.EmailMessage, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            LogSendFailed((int)response.StatusCode, message.Subject);
            response.EnsureSuccessStatusCode();
        }

        LogSent(message.Subject, message.Recipients.Count);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Email dispatched to mail service: subject={Subject} recipients={RecipientCount}")]
    private partial void LogSent(string subject, int recipientCount);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Email send via mail service failed with status {StatusCode} for subject={Subject}")]
    private partial void LogSendFailed(int statusCode, string subject);
}
