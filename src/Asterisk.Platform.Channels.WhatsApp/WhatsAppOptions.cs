namespace Asterisk.Platform.Channels.WhatsApp;

/// <summary>Options for the WhatsApp Meta Business API connector.</summary>
public sealed class WhatsAppOptions
{
    /// <summary>The WhatsApp Business phone number ID.</summary>
    public required string PhoneNumberId { get; set; }

    /// <summary>Meta API access token (Bearer).</summary>
    public required string AccessToken { get; set; }

    /// <summary>Token used to verify the webhook subscription challenge.</summary>
    public required string WebhookVerifyToken { get; set; }

    /// <summary>App secret used to validate HMAC-SHA256 webhook signatures.</summary>
    public required string AppSecret { get; set; }

    /// <summary>Meta Graph API version. Default: v21.0.</summary>
    public string ApiVersion { get; set; } = "v21.0";

    /// <summary>Meta Graph API base URL. Default: https://graph.facebook.com.</summary>
    public string BaseUrl { get; set; } = "https://graph.facebook.com";
}
