using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Twitter;

/// <summary>
/// Transforms MessageEnvelope blocks for Twitter/X DM delivery.
/// X DM API v2 constraints:
/// <list type="bullet">
///   <item>Text truncated to 10,000 characters.</item>
///   <item>ImageBlock and FileBlock are not supported via DM API v2 — converted to a text description.</item>
///   <item>InteractiveBlock quick replies: maximum 20 options, label max 36 characters.</item>
/// </list>
/// </summary>
public sealed class TwitterMessageTransformer : IMessageTransformer
{
    public ChannelType TargetChannel => ChannelType.Twitter;

    public MessageEnvelope Transform(MessageEnvelope source)
    {
        if (source.Blocks.Count == 0)
            return source;

        var transformed = new List<MessageBlock>(source.Blocks.Count);
        foreach (var block in source.Blocks)
        {
            var result = TransformBlock(block);
            if (result is not null)
                transformed.Add(result);
        }

        return new MessageEnvelope(transformed);
    }

    private static MessageBlock? TransformBlock(MessageBlock block) => block switch
    {
        TextBlock text => TransformText(text),
        ImageBlock image => ImageToText(image),
        FileBlock file => FileToText(file),
        InteractiveBlock interactive => TransformInteractive(interactive),
        _ => block,
    };

    private static TextBlock TransformText(TextBlock text)
    {
        const int maxLength = 10_000;
        if (text.Text.Length <= maxLength)
            return text;
        return new TextBlock(text.Text[..maxLength]);
    }

    private static TextBlock? ImageToText(ImageBlock image)
    {
        // X DM API v2 does not support direct image attachment in DMs without media upload.
        // Convert to a text description so the message is not silently dropped.
        if (image.Caption is not null)
            return new TextBlock($"[Image: {image.Caption}] {image.Url}");

        return new TextBlock($"[Image] {image.Url}");
    }

    private static TextBlock? FileToText(FileBlock file)
    {
        // X DM API v2 does not support file attachments in DMs.
        return new TextBlock($"[File: {file.FileName}] {file.Url}");
    }

    private static InteractiveBlock TransformInteractive(InteractiveBlock interactive)
    {
        const int maxOptions = 20;
        const int maxLabelLength = 36;

        var replies = interactive.Replies
            .Take(maxOptions)
            .Select(r => r.Title.Length <= maxLabelLength
                ? r
                : r with { Title = r.Title[..maxLabelLength] })
            .ToList();

        if (replies.Count == interactive.Replies.Count &&
            replies.Zip(interactive.Replies).All(p => ReferenceEquals(p.First, p.Second)))
            return interactive;

        return new InteractiveBlock(interactive.Body, replies);
    }
}
