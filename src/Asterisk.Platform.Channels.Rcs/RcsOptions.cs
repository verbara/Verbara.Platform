namespace Asterisk.Platform.Channels.Rcs;

public sealed class RcsOptions
{
    /// <summary>RBM agent identifier registered in the Google Business Communications console.</summary>
    public required string AgentId { get; set; }

    /// <summary>Google service account credentials JSON for authenticating with the RBM API.</summary>
    public required string ServiceAccountJson { get; set; }

    public string BaseUrl { get; set; } = "https://rcsbusinessmessaging.googleapis.com";
    public string ApiVersion { get; set; } = "v1";
}
