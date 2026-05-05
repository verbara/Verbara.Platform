namespace Verbara.Platform.Core.Email;

public interface IEmailService
{
    ValueTask SendAsync(EmailMessage message, CancellationToken ct);
}
