namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// Process-wide options for the WhatsApp Meta Business API channel. Every credential (access token,
/// phone number id, app secret, webhook verify token) is per tenant only — see
/// <see cref="WhatsAppCredentialKeys"/>.
/// </summary>
public sealed class WhatsAppOptions
{
    /// <summary>Meta Graph API version. Default: v21.0.</summary>
    public string ApiVersion { get; set; } = "v21.0";

    /// <summary>Meta Graph API base URL. Default: https://graph.facebook.com.</summary>
    public string BaseUrl { get; set; } = "https://graph.facebook.com";
}
