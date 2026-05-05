using Verbara.Platform.Core;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Media;

/// <summary>
/// Local-disk implementation of <see cref="IMediaStorage"/>.
/// Files are stored under <c>{BasePath}/{tenantId}/{fileName}</c> with a unique prefix to avoid collisions.
/// </summary>
public sealed class FileSystemMediaStorage : IMediaStorage
{
    private readonly MediaOptions _options;

    /// <summary>Initialises a new instance with the given options.</summary>
    public FileSystemMediaStorage(IOptions<MediaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<string> UploadAsync(TenantId tenantId, string fileName, string contentType, Stream content, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(content);

        var tenantDir = Path.Combine(_options.BasePath, tenantId.Value);
        Directory.CreateDirectory(tenantDir);

        var uniqueName = $"{Guid.NewGuid():N}_{fileName}";
        var filePath = Path.Combine(tenantDir, uniqueName);

        await using var fileStream = File.Create(filePath);
        await content.CopyToAsync(fileStream, ct).ConfigureAwait(false);

        return filePath;
    }

    /// <inheritdoc />
    public Task<Stream?> DownloadAsync(string storagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        if (!File.Exists(storagePath))
            return Task.FromResult<Stream?>(null);

        Stream stream = File.OpenRead(storagePath);
        return Task.FromResult<Stream?>(stream);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        if (File.Exists(storagePath))
            File.Delete(storagePath);

        return Task.CompletedTask;
    }
}
