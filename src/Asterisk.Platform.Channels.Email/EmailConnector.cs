using System.Net.Mail;
using System.Net.Mime;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Email;

/// <summary>
/// Sends outbound emails via SMTP. Converts <see cref="MessageEnvelope"/> blocks to MIME:
/// <list type="bullet">
///   <item><see cref="TextBlock"/> → plain-text body</item>
///   <item><see cref="ImageBlock"/> → attachment</item>
///   <item><see cref="FileBlock"/> → attachment</item>
/// </list>
/// The <see cref="OutboundMessage.TemplateId"/> field is interpreted as the email subject.
/// Threading headers (<c>In-Reply-To</c>, <c>References</c>) are supplied via
/// <see cref="IEmailThreadingContext"/>.
/// </summary>
public sealed class EmailConnector : IChannelConnector
{
    private readonly EmailOptions _options;
    private readonly ISmtpClientWrapper _smtp;
    private readonly IEmailThreadingContext _threading;
    private readonly ILogger<EmailConnector> _logger;

    public ChannelType Channel => ChannelType.Email;

    public EmailConnector(
        ISmtpClientWrapper smtp,
        IOptions<EmailOptions> options,
        IEmailThreadingContext threading,
        ILogger<EmailConnector> logger)
    {
        _smtp = smtp;
        _options = options.Value;
        _threading = threading;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var toAddress = message.To.Address;
        var subject = message.TemplateId ?? "Message";

        Log.SendingEmail(_logger, toAddress, subject);

        var messageId = $"<{Guid.NewGuid()}@{GetDomain()}>";

        // Retrieve threading context for this conversation
        var threadInfo = _threading.GetThreadInfo(message.ConversationId);

        using var mail = new MailMessage();
        mail.From = _options.DefaultFromName is not null
            ? new MailAddress(_options.DefaultFromAddress, _options.DefaultFromName)
            : new MailAddress(_options.DefaultFromAddress);
        mail.To.Add(toAddress);
        mail.Subject = subject;

        mail.Headers.Add("Message-ID", messageId);

        if (threadInfo?.InReplyTo is not null)
            mail.Headers.Add("In-Reply-To", threadInfo.InReplyTo);

        if (threadInfo?.References is not null)
            mail.Headers.Add("References", threadInfo.References);

        // Build body and attachments from blocks
        var textParts = new List<string>();

        foreach (var block in message.Content.Blocks)
        {
            switch (block)
            {
                case TextBlock text:
                    textParts.Add(text.Text);
                    break;

                case ImageBlock image:
                    var imgName = ExtractFileName(image.Url) ?? "image";
                    AddUrlAttachment(mail, image.Url, imgName, image.MimeType ?? MediaTypeNames.Image.Jpeg);
                    if (image.Caption is not null)
                        textParts.Add(image.Caption);
                    break;

                case FileBlock file:
                    if (file.SizeBytes.HasValue)
                    {
                        var maxBytes = (long)_options.MaxAttachmentSizeMb * 1024 * 1024;
                        if (file.SizeBytes.Value > maxBytes)
                        {
                            Log.AttachmentTooLarge(_logger, file.FileName, _options.MaxAttachmentSizeMb);
                            continue;
                        }
                    }

                    AddUrlAttachment(mail, file.Url, file.FileName, file.MimeType ?? MediaTypeNames.Application.Octet);
                    break;

                case AudioBlock audio:
                    var audioName = ExtractFileName(audio.Url) ?? "audio";
                    AddUrlAttachment(mail, audio.Url, audioName, audio.MimeType ?? "audio/mpeg");
                    break;

                case VideoBlock video:
                    var videoName = ExtractFileName(video.Url) ?? "video";
                    AddUrlAttachment(mail, video.Url, videoName, video.MimeType ?? "video/mp4");
                    if (video.Caption is not null)
                        textParts.Add(video.Caption);
                    break;
            }
        }

        mail.Body = string.Join("\n\n", textParts);
        mail.IsBodyHtml = false;

        try
        {
            await _smtp.SendAsync(mail, ct).ConfigureAwait(false);
            Log.EmailSent(_logger, toAddress, messageId);
            return new SendResult(true, messageId, null, null);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or OperationCanceledException)
        {
            Log.SmtpError(_logger, ex, toAddress);
            return new SendResult(false, null, "SMTP_ERROR", ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        // SMTP does not support delivery status polling; status arrives via DSN or MDA callbacks.
        return Task.FromResult<MessageDeliveryStatus?>(null);
    }

    private static void AddUrlAttachment(MailMessage mail, string url, string fileName, string mimeType)
    {
        // Store the URL as the attachment content — real implementations would download the resource.
        var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(url));
        mail.Attachments.Add(new Attachment(data, fileName, mimeType));
    }

    private string GetDomain()
    {
        var at = _options.DefaultFromAddress.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? _options.DefaultFromAddress[(at + 1)..] : "mail.local";
    }

    private static string? ExtractFileName(string url)
    {
        var lastSlash = url.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < url.Length - 1
            ? url[(lastSlash + 1)..]
            : null;
    }
}
