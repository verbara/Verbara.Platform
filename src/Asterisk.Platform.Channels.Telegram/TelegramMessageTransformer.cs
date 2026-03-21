using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Telegram;

/// <summary>
/// Transforms MessageEnvelope blocks for Telegram delivery.
/// Applies Telegram-specific constraints:
/// <list type="bullet">
///   <item>Text truncated to 4096 characters (Bot API limit).</item>
///   <item>Caption truncated to 1024 characters (Bot API limit for media captions).</item>
///   <item>Inline keyboard button text truncated to 64 characters (Bot API limit).</item>
/// </list>
/// </summary>
public sealed class TelegramMessageTransformer : IMessageTransformer
{
    public ChannelType TargetChannel => ChannelType.Telegram;

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
        TextBlock text => TransformText(text),
        ImageBlock image => TransformImage(image),
        VideoBlock video => TransformVideo(video),
        InteractiveBlock interactive => TransformInteractive(interactive),
        _ => block,
    };

    private static TextBlock TransformText(TextBlock text)
    {
        const int maxLength = 4096;
        if (text.Text.Length <= maxLength)
            return text;
        return new TextBlock(text.Text[..maxLength]);
    }

    private static ImageBlock TransformImage(ImageBlock image)
    {
        const int maxCaptionLength = 1024;
        if (image.Caption is null || image.Caption.Length <= maxCaptionLength)
            return image;
        return image with { Caption = image.Caption[..maxCaptionLength] };
    }

    private static VideoBlock TransformVideo(VideoBlock video)
    {
        const int maxCaptionLength = 1024;
        if (video.Caption is null || video.Caption.Length <= maxCaptionLength)
            return video;
        return video with { Caption = video.Caption[..maxCaptionLength] };
    }

    private static InteractiveBlock TransformInteractive(InteractiveBlock interactive)
    {
        const int maxButtonTextLength = 64;

        var replies = interactive.Replies
            .Select(r => r.Title.Length <= maxButtonTextLength
                ? r
                : r with { Title = r.Title[..maxButtonTextLength] })
            .ToList();

        if (replies.Zip(interactive.Replies).All(p => ReferenceEquals(p.First, p.Second)))
            return interactive;

        return new InteractiveBlock(interactive.Body, replies);
    }
}
