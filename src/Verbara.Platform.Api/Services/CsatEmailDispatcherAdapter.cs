using Verbara.Platform.Core.Email;
using Verbara.Sdk.Pro.CsatRunner.Contracts;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Platform's in-process implementation of the Pro-defined
/// <see cref="ICsatEmailDispatcher"/> seam (csat-runner Phase E2, task 5b.2; Platform/ADR-0020 +
/// verbara-meta/ADR-0005 open-core boundary). Pro's email channel adapter calls
/// <see cref="SendAsync"/> via DI — same process, NOT an API call — to send a locale-templated CSAT
/// request email whose <c>Reply-To</c> header is the tokenized <c>csat+{token}@{ReplyToDomain}</c>
/// address (HMAC-signed over <c>(tenantId, conversationId, surveyResponseId)</c> by Pro's adapter),
/// which Platform's inbound IMAP reply parser (Phase C) later verifies.
/// </summary>
/// <remarks>
/// Bridges to <see cref="IEmailService.SendAsync"/>. Platform's <see cref="EmailMessage"/> carries no
/// generic header bag, so the Reply-To address rides the additive
/// <see cref="EmailMessage.ReplyToAddress"/> property (Phase E2) and the concrete sender
/// (<c>SmtpSender</c> in the Mail microservice) stamps <c>mime.ReplyTo</c> at send time. The gates
/// (license / queue-enabled / sampling) all run upstream in the Pro orchestrator.
/// </remarks>
internal sealed class CsatEmailDispatcherAdapter : ICsatEmailDispatcher
{
    private readonly IEmailService _emailService;

    public CsatEmailDispatcherAdapter(IEmailService emailService)
    {
        ArgumentNullException.ThrowIfNull(emailService);
        _emailService = emailService;
    }

    public async ValueTask SendAsync(CsatEmailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var message = new EmailMessage
        {
            Recipients = [new EmailRecipient(request.RecipientAddress)],
            Subject = request.Subject,
            TextBody = request.Body,
            ReplyToAddress = request.ReplyToAddress,
        };

        await _emailService.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
