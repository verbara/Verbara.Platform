namespace Asterisk.Platform.Core.Email;

public interface IEmailService
{
    ValueTask SendAsync(EmailMessage message, CancellationToken ct);
}
