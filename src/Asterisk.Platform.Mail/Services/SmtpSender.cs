using Asterisk.Platform.Core.Email;
using Asterisk.Sdk.Resilience;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Asterisk.Platform.Mail.Services;

internal sealed partial class SmtpSender : IEmailService
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps SMTP send attempts.
    /// Registered in Program.cs with retry 2 / baseDelay 1s, per-attempt timeout 15s,
    /// circuit breaker 3/60s — see the DI registration for exact budgets.
    /// </summary>
    public const string ResiliencePolicyKey = "smtp.send";

    private readonly SmtpOptions _options;
    private readonly ResiliencePolicy _policy;
    private readonly Func<MimeMessage, CancellationToken, Task> _sendMime;
    private readonly ILogger<SmtpSender> _logger;

    public SmtpSender(
        IOptions<SmtpOptions> options,
        ILogger<SmtpSender> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _options = options.Value;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
        _sendMime = SendMimeAsync;
    }

    /// <summary>
    /// Test-only constructor that replaces the MailKit-backed send with an injected delegate,
    /// allowing unit tests to verify retry/policy behaviour without a real SMTP endpoint.
    /// </summary>
    internal SmtpSender(
        IOptions<SmtpOptions> options,
        ILogger<SmtpSender> logger,
        ResiliencePolicy policy,
        Func<MimeMessage, CancellationToken, Task> sendMime)
    {
        _options = options.Value;
        _logger = logger;
        _policy = policy;
        _sendMime = sendMime;
    }

    public async ValueTask SendAsync(EmailMessage message, CancellationToken ct)
    {
        var mime = BuildMimeMessage(message);

        await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                await _sendMime(mime, innerCt).ConfigureAwait(false);
                return 0; // ValueTask<T> requires a return value
            },
            ct).ConfigureAwait(false);

        LogSent(message.Subject, message.Recipients.Count);
    }

    private async Task SendMimeAsync(MimeMessage mime, CancellationToken ct)
    {
        using var client = new SmtpClient();
        var secureSocketOptions = _options.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(_options.Host, _options.Port, secureSocketOptions, ct);
        if (_options.Username is not null && _options.Password is not null)
            await client.AuthenticateAsync(_options.Username, _options.Password, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    private MimeMessage BuildMimeMessage(EmailMessage message)
    {
        var mime = new MimeMessage();
        var fromName = message.FromName ?? _options.FromName;
        var fromAddress = message.FromAddress ?? _options.FromAddress;
        mime.From.Add(new MailboxAddress(fromName, fromAddress));
        foreach (var r in message.Recipients)
            mime.To.Add(new MailboxAddress(r.Name ?? r.Email, r.Email));
        mime.Subject = message.Subject;
        var builder = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        if (message.Attachments is not null)
        {
            foreach (var attachment in message.Attachments)
                builder.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }
        mime.Body = builder.ToMessageBody();
        return mime;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email sent: subject={Subject} recipients={RecipientCount}")]
    private partial void LogSent(string subject, int recipientCount);
}
