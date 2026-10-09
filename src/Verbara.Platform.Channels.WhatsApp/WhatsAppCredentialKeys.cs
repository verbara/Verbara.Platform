namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// The canonical keys of a tenant's WhatsApp channel configuration
/// (<c>TenantChannelConfig.Credentials</c>). Every WhatsApp credential is per tenant; there is no
/// process-wide fallback (whatsapp-works-for-real D5, operator decision DQ2).
/// </summary>
public static class WhatsAppCredentialKeys
{
    /// <summary>Meta Graph API access token (Bearer) used to send messages.</summary>
    public const string AccessToken = "AccessToken";

    /// <summary>The WhatsApp Business phone number id; inbound changes for any other id are ignored.</summary>
    public const string PhoneNumberId = "PhoneNumberId";

    /// <summary>The Meta app secret that signs webhook bodies (<c>X-Hub-Signature-256</c>).</summary>
    public const string AppSecret = "AppSecret";

    /// <summary>The token Meta echoes in the GET subscription handshake (<c>hub.verify_token</c>).</summary>
    public const string WebhookVerifyToken = "WebhookVerifyToken";

    /// <summary>Optional: the WhatsApp Business Account id.</summary>
    public const string WabaId = "WabaId";

    /// <summary>Optional: the Graph API version, e.g. <c>v21.0</c>.</summary>
    public const string ApiVersion = "ApiVersion";
}
