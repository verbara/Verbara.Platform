using System.Text.Json.Serialization;
using Asterisk.Platform.Core.Email;
using Asterisk.Platform.Mail.Endpoints;
using Asterisk.Platform.Mail.Services;

namespace Asterisk.Platform.Mail;

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
internal sealed partial class MailJsonContext : JsonSerializerContext;
