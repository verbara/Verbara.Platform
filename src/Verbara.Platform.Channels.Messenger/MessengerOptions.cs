namespace Verbara.Platform.Channels.Messenger;

/// <summary>Options for the Facebook Messenger connector.</summary>
public sealed class MessengerOptions
{
    /// <summary>The Facebook Page ID that owns this Messenger channel.</summary>
    public required string PageId { get; set; }

    /// <summary>Page Access Token used to send messages via the Meta Graph API.</summary>
    public required string PageAccessToken { get; set; }

    /// <summary>App secret used to validate HMAC-SHA256 webhook signatures.</summary>
    public required string AppSecret { get; set; }

    /// <summary>Token used to verify the webhook subscription challenge.</summary>
    public required string WebhookVerifyToken { get; set; }

    /// <summary>Meta Graph API version. Default: v21.0.</summary>
    public string ApiVersion { get; set; } = "v21.0";

    /// <summary>Meta Graph API base URL. Default: https://graph.facebook.com.</summary>
    public string BaseUrl { get; set; } = "https://graph.facebook.com";
}
