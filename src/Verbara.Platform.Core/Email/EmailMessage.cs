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
}

public sealed record EmailRecipient(string Email, string? Name = null);

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);
