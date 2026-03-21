using System.Text.Json.Serialization;

namespace Asterisk.Platform.Conversations;

public enum MessageBlockType
{
    Text = 0,
    Image = 1,
    Audio = 2,
    Video = 3,
    File = 4,
    Location = 5,
    Interactive = 6,
}

[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ImageBlock), "image")]
[JsonDerivedType(typeof(AudioBlock), "audio")]
[JsonDerivedType(typeof(VideoBlock), "video")]
[JsonDerivedType(typeof(FileBlock), "file")]
[JsonDerivedType(typeof(LocationBlock), "location")]
[JsonDerivedType(typeof(InteractiveBlock), "interactive")]
public abstract record MessageBlock(MessageBlockType Type);

public sealed record TextBlock(string Text) : MessageBlock(MessageBlockType.Text);

public sealed record ImageBlock(string Url, string? Caption, string? MimeType)
    : MessageBlock(MessageBlockType.Image);

public sealed record AudioBlock(string Url, TimeSpan? Duration, string? MimeType)
    : MessageBlock(MessageBlockType.Audio);

public sealed record VideoBlock(string Url, string? Caption, string? MimeType)
    : MessageBlock(MessageBlockType.Video);

public sealed record FileBlock(string Url, string FileName, string? MimeType, long? SizeBytes)
    : MessageBlock(MessageBlockType.File);

public sealed record LocationBlock(double Latitude, double Longitude, string? Name)
    : MessageBlock(MessageBlockType.Location);

public sealed record InteractiveBlock(string Body, IReadOnlyList<QuickReply> Replies)
    : MessageBlock(MessageBlockType.Interactive);

public sealed record QuickReply(string Id, string Title);
