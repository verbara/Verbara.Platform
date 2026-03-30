namespace Asterisk.Platform.Identity;

/// <summary>Classifies API keys by their authorization scope.</summary>
public enum ApiKeyType
{
    /// <summary>Standard tenant-scoped API key (current behavior).</summary>
    Standard = 0,

    /// <summary>Management API key — platform-scoped, authorized for platform:* operations.</summary>
    Management = 1,
}
