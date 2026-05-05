using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// Transforms MessageEnvelope blocks between canonical form and
/// WhatsApp-specific form. Currently a pass-through: the canonical
/// block types (TextBlock, ImageBlock, InteractiveBlock, etc.) map
/// 1:1 to the Meta API types that WhatsAppConnector serializes.
/// Useful as an extension point for WhatsApp-specific normalization
/// (e.g. truncating captions, sanitizing phone numbers).
/// </summary>
public sealed class WhatsAppMessageTransformer : IMessageTransformer
{
    public ChannelType TargetChannel => ChannelType.WhatsApp;

    /// <summary>
    /// Transforms the message envelope for WhatsApp delivery.
    /// Applies WhatsApp-specific constraints:
    /// <list type="bullet">
    ///   <item>Text body truncated to 4096 characters (Meta limit).</item>
    ///   <item>Interactive button titles truncated to 20 characters (Meta limit).</item>
    ///   <item>Button count capped at 3 (Meta limit).</item>
    /// </list>
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
        TextBlock text => TransformText(text),
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

    private static InteractiveBlock TransformInteractive(InteractiveBlock interactive)
    {
        const int maxButtonTitleLength = 20;
        const int maxButtons = 3;

        var replies = interactive.Replies
            .Take(maxButtons)
            .Select(r => r.Title.Length <= maxButtonTitleLength
                ? r
                : r with { Title = r.Title[..maxButtonTitleLength] })
            .ToList();

        if (replies.Count == interactive.Replies.Count &&
            replies.Zip(interactive.Replies).All(p => ReferenceEquals(p.First, p.Second)))
            return interactive;

        return new InteractiveBlock(interactive.Body, replies);
    }
}
