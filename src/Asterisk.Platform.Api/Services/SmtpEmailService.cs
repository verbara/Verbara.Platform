using Asterisk.Platform.Core.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class SmtpEmailService : IEmailService
{
    private const int MaxAttempts = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<SmtpOptions> options, ILogger<SmtpEmailService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask SendAsync(EmailMessage message, CancellationToken ct)
    {
        var mime = BuildMimeMessage(message);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await SendMimeAsync(mime, ct);
                LogSent(message.Subject, message.Recipients.Count);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                LogRetry(attempt, message.Subject, ex.Message);
                await Task.Delay(RetryDelay, ct);
            }
        }

        // Final attempt without catch — let exception propagate
        await SendMimeAsync(mime, ct);
        LogSent(message.Subject, message.Recipients.Count);
    }

    private async Task SendMimeAsync(MimeMessage mime, CancellationToken ct)
    {
        using var client = new SmtpClient();

        var secureSocketOptions = _options.UseTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

        await client.ConnectAsync(_options.Host, _options.Port, secureSocketOptions, ct);

        if (_options.Username is not null && _options.Password is not null)
            await client.AuthenticateAsync(_options.Username, _options.Password, ct);

        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    private MimeMessage BuildMimeMessage(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));

        foreach (var r in message.Recipients)
            mime.To.Add(new MailboxAddress(r.Name ?? r.Email, r.Email));

        mime.Subject = message.Subject;

        var builder = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody
        };

        if (message.Attachments is not null)
        {
            foreach (var attachment in message.Attachments)
                builder.Attachments.Add(attachment.FileName, attachment.Content,
                    ContentType.Parse(attachment.ContentType));
        }

        mime.Body = builder.ToMessageBody();
        return mime;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Email sent: subject={Subject} recipients={RecipientCount}")]
    private partial void LogSent(string subject, int recipientCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Email send attempt {Attempt} failed for subject={Subject}: {Error} — retrying")]
    private partial void LogRetry(int attempt, string subject, string error);
}
