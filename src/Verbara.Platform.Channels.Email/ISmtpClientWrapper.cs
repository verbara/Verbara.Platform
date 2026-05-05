using System.Net.Mail;

namespace Verbara.Platform.Channels.Email;

/// <summary>
/// Abstraction over <see cref="SmtpClient"/> to allow unit testing without real SMTP.
/// </summary>
public interface ISmtpClientWrapper : IDisposable
{
    Task SendAsync(MailMessage message, CancellationToken ct);
}
