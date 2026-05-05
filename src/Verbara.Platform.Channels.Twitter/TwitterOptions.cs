namespace Verbara.Platform.Channels.Twitter;

/// <summary>Options for the Twitter/X DM API v2 connector.</summary>
public sealed class TwitterOptions
{
    /// <summary>Twitter API v2 API key (consumer key).</summary>
    public required string ApiKey { get; set; }

    /// <summary>Twitter API v2 API secret (consumer secret).</summary>
    public required string ApiSecret { get; set; }

    /// <summary>OAuth 1.0a access token for the connected account.</summary>
    public required string AccessToken { get; set; }

    /// <summary>OAuth 1.0a access token secret for the connected account.</summary>
    public required string AccessTokenSecret { get; set; }

    /// <summary>Bearer token for app-only authentication (used for DM sends).</summary>
    public required string BearerToken { get; set; }

    /// <summary>X API base URL. Default: https://api.x.com.</summary>
    public string BaseUrl { get; set; } = "https://api.x.com";

    /// <summary>X API version prefix. Default: 2.</summary>
    public string ApiVersion { get; set; } = "2";
}
