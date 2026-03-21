using System.Text.Json.Serialization;

namespace Asterisk.Platform.Channels.Rcs;

/// <summary>Incoming Google RBM webhook payload.</summary>
internal sealed class RbmWebhookPayload
{
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("deliveryReceipt")]
    public RbmDeliveryReceipt? DeliveryReceipt { get; set; }

    [JsonPropertyName("senderPhoneNumber")]
    public string? SenderPhoneNumber { get; set; }

    [JsonPropertyName("sendTime")]
    public string? SendTime { get; set; }
}

internal sealed class RbmDeliveryReceipt
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }
}
