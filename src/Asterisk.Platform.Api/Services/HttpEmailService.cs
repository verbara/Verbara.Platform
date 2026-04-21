using System.Net.Http.Json;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Core.Email;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class HttpEmailService : IEmailService
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps the HTTP
    /// call to the Mail microservice. Shared with <see cref="HttpEmailTemplateService"/>
    /// — registered once in Program.cs with circuit 5/45s + retry 3/300ms + timeout 10s.
    /// </summary>
    public const string ResiliencePolicyKey = "channel.email-http";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpEmailService> _logger;
    private readonly ResiliencePolicy _policy;

    public HttpEmailService(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpEmailService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public async ValueTask SendAsync(EmailMessage message, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient("mail");
        using var response = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt => await client.PostAsJsonAsync(
                "/api/v1/send", message, ApiJsonContext.Default.EmailMessage, innerCt).ConfigureAwait(false),
            ct).ConfigureAwait(false);

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
