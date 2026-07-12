namespace Verbara.Platform.Core.Email;

public sealed class EmailMessage
{
    public required IReadOnlyList<EmailRecipient> Recipients { get; init; }
    public required string Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public IReadOnlyList<EmailAttachment>? Attachments { get; init; }
    public string? FromName { get; init; }
    public string? FromAddress { get; init; }

    /// <summary>
    /// Optional <c>Reply-To</c> address set on the outbound message at send time. Additive and
    /// nullable (back-compat: pre-existing callers leave it null and get no Reply-To header).
    /// Consumed by the CSAT email dispatch seam (csat-runner Phase E2): Pro's
    /// <c>CsatEmailRequest.ReplyToAddress</c> carries the tokenized
    /// <c>csat+{token}@{ReplyToDomain}</c> address, which Platform maps here so the concrete
    /// sender stamps the header — the <see cref="EmailMessage"/> DTO carries no other header bag.
    /// </summary>
    public string? ReplyToAddress { get; init; }
}

public sealed record EmailRecipient(string Email, string? Name = null);

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);
