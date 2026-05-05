using Verbara.Platform.Core;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Media;

/// <summary>
/// Default implementation of <see cref="IMediaService"/>.
/// Validates content type and size, uploads via <see cref="IMediaStorage"/>, and persists metadata via <see cref="IMediaStore"/>.
/// </summary>
public sealed class DefaultMediaService : IMediaService
{
    private readonly IMediaStorage _storage;
    private readonly IMediaStore _store;
    private readonly IClock _clock;
    private readonly MediaOptions _options;

    /// <summary>Initialises a new instance with the given dependencies.</summary>
    public DefaultMediaService(IMediaStorage storage, IMediaStore store, IClock clock, IOptions<MediaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _storage = storage;
        _store = store;
        _clock = clock;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<MediaFile> UploadAsync(
        TenantId tenantId,
        string fileName,
        string contentType,
        Stream content,
        EntityId? conversationId = null,
        string? uploadedBy = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(content);

        if (!_options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Content type '{contentType}' is not allowed.");

        var maxBytes = _options.MaxFileSizeMb * 1024 * 1024;
        if (content.CanSeek && content.Length > maxBytes)
            throw new InvalidOperationException($"File size exceeds the maximum allowed size of {_options.MaxFileSizeMb} MB.");

        // Buffer content to measure size when stream is not seekable.
        var (measuredStream, sizeBytes) = await MeasureAsync(content, maxBytes, ct).ConfigureAwait(false);

        var storagePath = await _storage.UploadAsync(tenantId, fileName, contentType, measuredStream, ct)
            .ConfigureAwait(false);

        var file = new MediaFile
        {
            FileId = EntityId.New(),
            TenantId = tenantId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            StoragePath = storagePath,
            ConversationId = conversationId,
            UploadedAt = _clock.UtcNow,
            UploadedBy = uploadedBy,
        };

        await _store.SaveAsync(file, ct).ConfigureAwait(false);
        return file;
    }

    /// <inheritdoc />
    public async Task<Stream?> DownloadAsync(TenantId tenantId, EntityId fileId, CancellationToken ct = default)
    {
        var file = await _store.GetByIdAsync(tenantId, fileId, ct).ConfigureAwait(false);
        if (file is null)
            return null;

        return await _storage.DownloadAsync(file.StoragePath, ct).ConfigureAwait(false);
    }

    private static async Task<(Stream Stream, long SizeBytes)> MeasureAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        if (source.CanSeek)
        {
            var size = source.Length;
            source.Position = 0;
            return (source, size);
        }

        // Buffer into a MemoryStream to measure; enforce size limit during buffering.
        var buffer = new MemoryStream();
        var buf = new byte[81920];
        int read;
        long total = 0;

        while ((read = await source.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException($"File size exceeds the maximum allowed size.");

            await buffer.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return (buffer, total);
    }
}
