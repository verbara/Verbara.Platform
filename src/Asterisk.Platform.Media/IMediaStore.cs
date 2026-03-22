using Asterisk.Platform.Core;

namespace Asterisk.Platform.Media;

/// <summary>
/// Persistence contract for <see cref="MediaFile"/> metadata.
/// Binary content is managed separately via <see cref="IMediaStorage"/>.
/// </summary>
public interface IMediaStore
{
    /// <summary>Returns the media file with the given identifier, or <c>null</c> if not found.</summary>
    Task<MediaFile?> GetByIdAsync(TenantId tenantId, EntityId fileId, CancellationToken ct);

    /// <summary>Persists or replaces media file metadata.</summary>
    Task SaveAsync(MediaFile file, CancellationToken ct);

    /// <summary>Removes media file metadata. Does not delete the underlying binary content.</summary>
    Task DeleteAsync(TenantId tenantId, EntityId fileId, CancellationToken ct);

    /// <summary>Returns all media files attached to the given conversation.</summary>
    Task<IReadOnlyList<MediaFile>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
}
