using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Instagram;

/// <summary>
/// Transforms MessageEnvelope blocks for Instagram DM delivery.
/// Applies Instagram-specific constraints:
/// <list type="bullet">
///   <item>Text body truncated to 1000 characters (Instagram limit).</item>
///   <item>Quick reply titles truncated to 20 characters.</item>
///   <item>Quick replies capped at 13.</item>
///   <item>InteractiveBlock maps to quick replies only — Instagram does not support button templates.</item>
/// </list>
/// </summary>
public sealed class InstagramMessageTransformer : IMessageTransformer
{
    public ChannelType TargetChannel => ChannelType.Instagram;

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
        InteractiveBlock interactive => TransformInteractive(interactive),
        _ => block,
    };

    private static TextBlock TransformText(TextBlock text)
    {
        const int maxLength = 1000;
        if (text.Text.Length <= maxLength)
            return text;
        return new TextBlock(text.Text[..maxLength]);
    }

    private static InteractiveBlock TransformInteractive(InteractiveBlock interactive)
    {
        const int maxTitleLength = 20;
        const int maxQuickReplies = 13;

        var replies = interactive.Replies
            .Take(maxQuickReplies)
            .Select(r => r.Title.Length <= maxTitleLength
                ? r
                : r with { Title = r.Title[..maxTitleLength] })
            .ToList();

        if (replies.Count == interactive.Replies.Count &&
            replies.Zip(interactive.Replies).All(p => ReferenceEquals(p.First, p.Second)))
            return interactive;

        return new InteractiveBlock(interactive.Body, replies);
    }
}
