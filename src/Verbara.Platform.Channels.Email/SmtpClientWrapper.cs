using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.Email;

/// <summary>Default <see cref="ISmtpClientWrapper"/> backed by <see cref="SmtpClient"/>.</summary>
public sealed class SmtpClientWrapper : ISmtpClientWrapper
{
    private readonly SmtpClient _client;

    public SmtpClientWrapper(IOptions<EmailOptions> options)
    {
        var opts = options.Value;
        _client = new SmtpClient(opts.SmtpHost, opts.SmtpPort)
        {
            EnableSsl = opts.UseSsl,
            Credentials = new NetworkCredential(opts.SmtpUsername, opts.SmtpPassword),
        };
    }

    public Task SendAsync(MailMessage message, CancellationToken ct)
    {
        // SmtpClient.SendMailAsync(MailMessage, CancellationToken) is available in .NET 6+.
        return _client.SendMailAsync(message, ct);
    }

    public void Dispose() => _client.Dispose();
}
