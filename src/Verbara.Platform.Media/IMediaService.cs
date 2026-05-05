using Verbara.Platform.Core;

namespace Verbara.Platform.Media;

/// <summary>
/// High-level service for uploading and downloading media files.
/// </summary>
public interface IMediaService
{
    /// <summary>
    /// Validates and uploads a file, persisting both binary content and metadata.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the content type is not allowed or the file exceeds the configured size limit.
    /// </exception>
    Task<MediaFile> UploadAsync(
        TenantId tenantId,
        string fileName,
        string contentType,
        Stream content,
        EntityId? conversationId = null,
        string? uploadedBy = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a readable stream for the given file, or <c>null</c> if the file does not exist.
    /// </summary>
    Task<Stream?> DownloadAsync(TenantId tenantId, EntityId fileId, CancellationToken ct = default);
}
