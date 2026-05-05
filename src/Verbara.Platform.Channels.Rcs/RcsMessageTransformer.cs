using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Rcs;

/// <summary>
/// Transforms a <see cref="MessageEnvelope"/> for RCS delivery.
/// RCS supports plain text, single rich cards, and carousels;
/// this transformer enforces RCS content limits:
/// <list type="bullet">
///   <item>Text messages truncated to 2048 characters (RBM limit).</item>
///   <item>Rich card titles truncated to 200 characters.</item>
///   <item>Rich card descriptions truncated to 2000 characters.</item>
///   <item>Suggestion labels truncated to 25 characters.</item>
/// </list>
/// </summary>
public sealed class RcsMessageTransformer : IMessageTransformer
{
    private const int MaxTextLength = 2048;
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 2000;
    private const int MaxSuggestionLabelLength = 25;

    public ChannelType TargetChannel => ChannelType.Rcs;

    public MessageEnvelope Transform(MessageEnvelope source)
    {
        if (source.Blocks.Count == 0)
            return source;

        var transformed = new List<MessageBlock>(source.Blocks.Count);
        foreach (var block in source.Blocks)
            transformed.Add(TransformBlock(block));

        return new MessageEnvelope(transformed);
    }

    private static MessageBlock TransformBlock(MessageBlock block) => block switch
    {
        TextBlock text => TransformText(text),
        ImageBlock image => TransformImage(image),
        InteractiveBlock interactive => TransformInteractive(interactive),
        _ => block,
    };

    private static TextBlock TransformText(TextBlock text)
    {
        if (text.Text.Length <= MaxTextLength)
            return text;
        return new TextBlock(text.Text[..MaxTextLength]);
    }

    private static ImageBlock TransformImage(ImageBlock image)
    {
        if (image.Caption is null || image.Caption.Length <= MaxTitleLength)
            return image;
        return image with { Caption = image.Caption[..MaxTitleLength] };
    }

    private static InteractiveBlock TransformInteractive(InteractiveBlock interactive)
    {
        var body = interactive.Body.Length <= MaxDescriptionLength
            ? interactive.Body
            : interactive.Body[..MaxDescriptionLength];

        var replies = interactive.Replies
            .Select(r => r.Title.Length <= MaxSuggestionLabelLength
                ? r
                : r with { Title = r.Title[..MaxSuggestionLabelLength] })
            .ToList();

        var bodyUnchanged = ReferenceEquals(body, interactive.Body);
        var repliesUnchanged = replies.Zip(interactive.Replies).All(p => ReferenceEquals(p.First, p.Second));

        if (bodyUnchanged && repliesUnchanged)
            return interactive;

        return new InteractiveBlock(body, replies);
    }
}
