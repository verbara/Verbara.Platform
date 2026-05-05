namespace Verbara.Platform.Media;

/// <summary>
/// Configuration options for the media service.
/// </summary>
public sealed class MediaOptions
{
    /// <summary>
    /// Root directory used by <see cref="FileSystemMediaStorage"/> to store files.
    /// Defaults to "media" (relative to the application base directory).
    /// </summary>
    public string BasePath { get; set; } = "media";

    /// <summary>
    /// Maximum allowed file size in megabytes. Defaults to 25 MB.
    /// </summary>
    public long MaxFileSizeMb { get; set; } = 25;

    /// <summary>
    /// MIME types that are accepted for upload. All other types are rejected.
    /// </summary>
    public IReadOnlyList<string> AllowedContentTypes { get; set; } =
    [
        "image/jpeg",
        "image/png",
        "image/gif",
        "audio/mpeg",
        "audio/ogg",
        "video/mp4",
        "application/pdf",
    ];
}
