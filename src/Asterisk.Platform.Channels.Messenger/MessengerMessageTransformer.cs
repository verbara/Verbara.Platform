using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Messenger;

/// <summary>
/// Transforms MessageEnvelope blocks for Facebook Messenger delivery.
/// Applies Messenger-specific constraints:
/// <list type="bullet">
///   <item>Text body truncated to 2000 characters (Messenger limit).</item>
///   <item>Quick reply titles truncated to 20 characters (Messenger limit).</item>
///   <item>Quick replies capped at 13 (Messenger limit).</item>
/// </list>
/// </summary>
public sealed class MessengerMessageTransformer : IMessageTransformer
{
    public ChannelType TargetChannel => ChannelType.Messenger;

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
        const int maxLength = 2000;
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
