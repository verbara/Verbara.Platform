using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Email;

/// <summary>
/// Transforms <see cref="MessageEnvelope"/> blocks for email delivery:
/// <list type="bullet">
///   <item><see cref="TextBlock"/> → kept as-is (maps to email plain-text body)</item>
///   <item><see cref="ImageBlock"/> → kept as-is (connector converts to attachment)</item>
///   <item><see cref="FileBlock"/> → kept as-is (connector converts to attachment)</item>
///   <item><see cref="InteractiveBlock"/> → flattened to <see cref="TextBlock"/> with option list</item>
///   <item><see cref="LocationBlock"/> → flattened to <see cref="TextBlock"/> with coordinates</item>
/// </list>
/// </summary>
public sealed class EmailMessageTransformer : IMessageTransformer
{
    public ChannelType TargetChannel => ChannelType.Email;

    /// <summary>
    /// Transforms the envelope for email delivery.
    /// Unsupported rich blocks (interactive, location) are converted to plain text.
    /// </summary>
    public MessageEnvelope Transform(MessageEnvelope source)
    {
        if (source.Blocks.Count == 0)
            return source;

        var transformed = new List<MessageBlock>(source.Blocks.Count);
        foreach (var block in source.Blocks)
        {
            transformed.Add(TransformBlock(block));
        }

        return new MessageEnvelope(transformed);
    }

    private static MessageBlock TransformBlock(MessageBlock block) => block switch
    {
        InteractiveBlock interactive => FlattenInteractive(interactive),
        LocationBlock location => FlattenLocation(location),
        AudioBlock audio => new FileBlock(audio.Url, ExtractFileName(audio.Url) ?? "audio", audio.MimeType, null),
        VideoBlock video => new FileBlock(video.Url, ExtractFileName(video.Url) ?? "video", video.MimeType, null),
        _ => block,
    };

    private static TextBlock FlattenInteractive(InteractiveBlock interactive)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(interactive.Body);

        for (var i = 0; i < interactive.Replies.Count; i++)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"{i + 1}. {interactive.Replies[i].Title}");
        }

        return new TextBlock(sb.ToString().TrimEnd());
    }

    private static TextBlock FlattenLocation(LocationBlock location)
    {
        var name = location.Name is not null ? $"{location.Name} — " : string.Empty;
        return new TextBlock(
            $"{name}Lat: {location.Latitude:F6}, Lon: {location.Longitude:F6}");
    }

    private static string? ExtractFileName(string url)
    {
        var lastSlash = url.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < url.Length - 1
            ? url[(lastSlash + 1)..]
            : null;
    }
}
