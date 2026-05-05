using Verbara.Platform.Core;

namespace Verbara.Platform.Media;

/// <summary>
/// Metadata for a file uploaded by a tenant. The binary content is stored separately via <see cref="IMediaStorage"/>.
/// </summary>
public sealed class MediaFile : ITenantScoped
{
    /// <summary>Unique identifier for this file.</summary>
    public required EntityId FileId { get; init; }

    /// <inheritdoc />
    public required TenantId TenantId { get; init; }

    /// <summary>Original file name as provided by the uploader.</summary>
    public required string FileName { get; init; }

    /// <summary>MIME type, e.g. "image/png".</summary>
    public required string ContentType { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Opaque path in the underlying storage backend returned by <see cref="IMediaStorage.UploadAsync"/>.</summary>
    public required string StoragePath { get; init; }

    /// <summary>Conversation this file is attached to, if any.</summary>
    public EntityId? ConversationId { get; init; }

    /// <summary>When the file was uploaded.</summary>
    public required DateTimeOffset UploadedAt { get; init; }

    /// <summary>User identifier who uploaded the file, if known.</summary>
    public string? UploadedBy { get; init; }
}
