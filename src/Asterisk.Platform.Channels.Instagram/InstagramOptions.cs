namespace Asterisk.Platform.Channels.Instagram;

/// <summary>Options for the Instagram DM connector.</summary>
public sealed class InstagramOptions
{
    /// <summary>The Instagram Business Account ID.</summary>
    public required string InstagramAccountId { get; set; }

    /// <summary>Page Access Token for the linked Facebook Page (same token as Messenger).</summary>
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
