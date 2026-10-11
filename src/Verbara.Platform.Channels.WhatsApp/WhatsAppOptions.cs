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

    /// <summary>
    /// Meta Graph API base URL, without a trailing <c>/</c>. Default: <see cref="WhatsAppBaseUrl.Default"/>.
    /// The host sets it from <see cref="WhatsAppBaseUrl.ConfigurationKey"/> through
    /// <see cref="WhatsAppBaseUrl.Resolve"/>.
    /// </summary>
    public string BaseUrl { get; set; } = WhatsAppBaseUrl.Default;
}
