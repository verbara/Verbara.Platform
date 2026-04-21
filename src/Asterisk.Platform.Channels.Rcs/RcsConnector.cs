using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Rcs;

/// <summary>
/// Sends outbound messages over the RCS Business Messaging channel.
/// Performs a capability check before attempting delivery; returns a
/// non-success result (rather than throwing) when the recipient's device
/// does not support RCS, allowing the caller to fall back to SMS.
/// </summary>
public sealed class RcsConnector : IChannelConnector
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps
    /// <see cref="IRcsProvider"/> calls (capability/send/status) — the innermost
    /// network I/O boundary for this connector. Registered via <c>AddRcs()</c>
    /// with circuit 5/60s + retry 2/500ms + timeout 15s.
    /// </summary>
    public const string ResiliencePolicyKey = "channel.rcs";

    private readonly IRcsProvider _provider;
    private readonly ResiliencePolicy _policy;

    public ChannelType Channel => ChannelType.Rcs;

    public RcsConnector(
        IRcsProvider provider,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _provider = provider;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var recipient = message.To.Address;

        var capability = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt => await _provider.CheckCapabilityAsync(recipient, innerCt).ConfigureAwait(false),
            ct).ConfigureAwait(false);
        if (!capability.IsRcsEnabled)
            return new SendResult(false, null, "recipient_not_rcs_enabled", "Recipient device does not support RCS.");

        var rcsMessage = BuildRcsMessage(message.Content);
        if (rcsMessage is null)
            return new SendResult(false, null, "UNSUPPORTED_CONTENT", "Message content could not be mapped to an RCS message type.");

        var result = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt => await _provider.SendMessageAsync(recipient, rcsMessage, innerCt).ConfigureAwait(false),
            ct).ConfigureAwait(false);
        return new SendResult(result.Success, result.MessageId, result.ErrorCode, null);
    }

    public async Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        var status = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt => await _provider.GetStatusAsync(externalMessageId, innerCt).ConfigureAwait(false),
            ct).ConfigureAwait(false);
        return MapStatus(status);
    }

    internal static RcsMessage? BuildRcsMessage(MessageEnvelope envelope)
    {
        if (envelope.Blocks.Count == 0)
            return null;

        // Multiple blocks → carousel of rich cards (one card per image/interactive block).
        // If only a single block, pick the most appropriate RCS message type directly.
        var richCards = new List<RcsRichCardMessage>();

        foreach (var block in envelope.Blocks)
        {
            switch (block)
            {
                case TextBlock text when envelope.Blocks.Count == 1:
                    return new RcsTextMessage(text.Text);

                case ImageBlock image:
                    richCards.Add(new RcsRichCardMessage(
                        image.Caption ?? string.Empty,
                        string.Empty,
                        image.Url,
                        []));
                    break;

                case InteractiveBlock interactive:
                    var suggestions = interactive.Replies
                        .Select(r => (RcsSuggestion)new RcsReplySuggestion(r.Title, r.Id))
                        .ToList();
                    richCards.Add(new RcsRichCardMessage(
                        interactive.Body,
                        string.Empty,
                        null,
                        suggestions));
                    break;

                case TextBlock text:
                    // In multi-block envelopes, wrap text as a minimal rich card.
                    richCards.Add(new RcsRichCardMessage(text.Text, string.Empty, null, []));
                    break;
            }
        }

        if (richCards.Count == 0)
            return null;

        return richCards.Count == 1 ? richCards[0] : new RcsCarouselMessage(richCards);
    }

    private static MessageDeliveryStatus? MapStatus(RcsDeliveryStatus status) =>
        status switch
        {
            RcsDeliveryStatus.Queued => MessageDeliveryStatus.Pending,
            RcsDeliveryStatus.Sent => MessageDeliveryStatus.Sent,
            RcsDeliveryStatus.Delivered => MessageDeliveryStatus.Delivered,
            RcsDeliveryStatus.Read => MessageDeliveryStatus.Delivered,
            RcsDeliveryStatus.Failed => MessageDeliveryStatus.Failed,
            _ => null,
        };
}
