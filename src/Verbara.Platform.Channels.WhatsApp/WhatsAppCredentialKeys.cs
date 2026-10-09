namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// Canonical credential keys of a tenant's WhatsApp channel configuration
/// (<c>tenant_channel_configs.credentials</c>). Credentials are per tenant only: there is no
/// process-wide fallback for any of them.
/// </summary>
public static class WhatsAppCredentialKeys
{
    /// <summary>Meta Graph API access token (Bearer) used to send as the tenant's business.</summary>
    public const string AccessToken = "AccessToken";

    /// <summary>The tenant's WhatsApp Business phone number id (sender, and inbound binding).</summary>
    public const string PhoneNumberId = "PhoneNumberId";

    /// <summary>Meta app secret used to validate the HMAC-SHA256 webhook signature.</summary>
    public const string AppSecret = "AppSecret";

    /// <summary>Token Meta echoes on the webhook subscription (GET) verification.</summary>
    public const string WebhookVerifyToken = "WebhookVerifyToken";

    /// <summary>Optional WhatsApp Business Account id.</summary>
    public const string WabaId = "WabaId";

    /// <summary>Optional Graph API version override (default: <see cref="WhatsAppOptions.ApiVersion"/>).</summary>
    public const string ApiVersion = "ApiVersion";
}
