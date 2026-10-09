namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// Process-wide options for the WhatsApp Meta Business API channel. The sending credentials
/// (access token, phone number id) are per tenant only — see <see cref="WhatsAppCredentialKeys"/>.
/// </summary>
public sealed class WhatsAppOptions
{
    /// <summary>Token used to verify the webhook subscription challenge.</summary>
    public required string WebhookVerifyToken { get; set; }

    /// <summary>App secret used to validate HMAC-SHA256 webhook signatures.</summary>
    public required string AppSecret { get; set; }

    /// <summary>Meta Graph API version. Default: v21.0.</summary>
    public string ApiVersion { get; set; } = "v21.0";

    /// <summary>Meta Graph API base URL. Default: https://graph.facebook.com.</summary>
    public string BaseUrl { get; set; } = "https://graph.facebook.com";
}
