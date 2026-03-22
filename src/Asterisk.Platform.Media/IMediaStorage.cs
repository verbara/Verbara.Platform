using Asterisk.Platform.Core;

namespace Asterisk.Platform.Media;

/// <summary>
/// Binary storage backend for media files. Decoupled from metadata persistence.
/// </summary>
public interface IMediaStorage
{
    /// <summary>
    /// Uploads binary content and returns an opaque storage path that can be passed to
    /// <see cref="DownloadAsync"/> and <see cref="DeleteAsync"/>.
    /// </summary>
    Task<string> UploadAsync(TenantId tenantId, string fileName, string contentType, Stream content, CancellationToken ct);

    /// <summary>
    /// Downloads binary content for the given storage path, or <c>null</c> if the file does not exist.
    /// </summary>
    Task<Stream?> DownloadAsync(string storagePath, CancellationToken ct);

    /// <summary>Removes the binary content at the given storage path.</summary>
    Task DeleteAsync(string storagePath, CancellationToken ct);
}
