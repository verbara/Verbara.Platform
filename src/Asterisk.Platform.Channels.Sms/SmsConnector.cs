using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Sms;

public sealed class SmsConnector : IChannelConnector
{
    private readonly ISmsProvider _provider;
    private readonly SmsOptions _options;

    public ChannelType Channel => ChannelType.Sms;

    public SmsConnector(ISmsProvider provider, IOptions<SmsOptions> options)
    {
        _provider = provider;
        _options = options.Value;
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var body = ExtractText(message.Content);
        if (body is null)
            return new SendResult(false, null, "UNSUPPORTED_CONTENT", "SMS only supports text blocks.");

        body = SmsSegmentCalculator.Truncate(body, _options.MaxSegments);

        var from = _options.DefaultFromNumber;
        var recipient = message.To.Address;

        var result = await _provider.SendAsync(from, recipient, body, ct).ConfigureAwait(false);
        return new SendResult(result.Success, result.MessageId, result.ErrorCode, result.ErrorMessage);
    }

    public async Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct)
    {
        var status = await _provider.GetStatusAsync(externalMessageId, ct).ConfigureAwait(false);
        return MapStatus(status);
    }

    private static string? ExtractText(MessageEnvelope envelope)
    {
        var parts = new System.Text.StringBuilder();
        var hasText = false;

        foreach (var block in envelope.Blocks)
        {
            if (block is TextBlock text)
            {
                if (hasText)
                    parts.Append(' ');
                parts.Append(text.Text);
                hasText = true;
            }
            // Non-text blocks (Image, Audio, Video, File, Location, Interactive) are skipped for SMS.
        }

        return hasText ? parts.ToString() : null;
    }

    private static MessageDeliveryStatus? MapStatus(SmsDeliveryStatus status) =>
        status switch
        {
            SmsDeliveryStatus.Queued => MessageDeliveryStatus.Pending,
            SmsDeliveryStatus.Sent => MessageDeliveryStatus.Sent,
            SmsDeliveryStatus.Delivered => MessageDeliveryStatus.Delivered,
            SmsDeliveryStatus.Failed => MessageDeliveryStatus.Failed,
            SmsDeliveryStatus.Undelivered => MessageDeliveryStatus.Failed,
            _ => null,
        };
}
