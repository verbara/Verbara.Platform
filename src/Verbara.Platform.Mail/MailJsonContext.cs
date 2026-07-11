using System.Text.Json.Serialization;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Mail.Endpoints;
using Verbara.Platform.Mail.Services;

namespace Verbara.Platform.Mail;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EmailMessage))]
[JsonSerializable(typeof(EmailRecipient))]
[JsonSerializable(typeof(EmailAttachment))]
[JsonSerializable(typeof(BrandingContext))]
[JsonSerializable(typeof(RenderTemplateRequest))]
[JsonSerializable(typeof(RenderTemplateResponse))]
[JsonSerializable(typeof(OAuthStatePayload))]
[JsonSerializable(typeof(MailMessageDto))]
[JsonSerializable(typeof(MailRecipientDto))]
[JsonSerializable(typeof(MailAttachmentDto))]
[JsonSerializable(typeof(SendMailRequest))]
[JsonSerializable(typeof(ReplyMailRequest))]
[JsonSerializable(typeof(ForwardMailRequest))]
[JsonSerializable(typeof(MailboxListResponse))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(IReadOnlyList<MailMessageDto>))]
[JsonSerializable(typeof(IReadOnlyList<MailAttachmentDto>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(CsatEmailCapturePayload))]
internal sealed partial class MailJsonContext : JsonSerializerContext;
