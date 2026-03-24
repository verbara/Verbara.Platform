using Amazon.S3;
using Amazon.S3.Model;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Media;

/// <summary>
/// S3-compatible implementation of <see cref="IMediaStorage"/>.
/// Supports AWS S3 and MinIO (path-style addressing).
/// Storage paths are object keys of the form <c>{tenantId}/{guid}_{fileName}</c>.
/// </summary>
public sealed class S3MediaStorage : IMediaStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucketName;

    /// <summary>
    /// Initialises a new instance targeting the given S3-compatible endpoint.
    /// </summary>
    /// <param name="bucketName">Target bucket. Must already exist.</param>
    /// <param name="serviceUrl">
    /// Full base URL of the S3 endpoint (e.g. <c>http://minio:9000</c>).
    /// Pass <c>https://s3.amazonaws.com</c> for native AWS.
    /// </param>
    /// <param name="region">AWS region name; defaults to <c>us-east-1</c> if omitted.</param>
    /// <param name="forcePathStyle">
    /// Set <c>true</c> for MinIO and other path-style endpoints.
    /// Set <c>false</c> (default) for virtual-hosted AWS buckets.
    /// </param>
    public S3MediaStorage(
        string bucketName,
        string serviceUrl,
        string? region = null,
        bool forcePathStyle = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceUrl);

        _bucketName = bucketName;

        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = forcePathStyle,
        };

        if (!string.IsNullOrWhiteSpace(region))
            config.AuthenticationRegion = region;

        _client = new AmazonS3Client(config);
    }

    /// <inheritdoc />
    public async Task<string> UploadAsync(
        TenantId tenantId,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(content);

        var key = $"{tenantId.Value}/{Guid.NewGuid():N}_{fileName}";

        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        };

        await _client.PutObjectAsync(request, ct).ConfigureAwait(false);

        return key;
    }

    /// <inheritdoc />
    public async Task<Stream?> DownloadAsync(string storagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        try
        {
            var request = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = storagePath,
            };

            var response = await _client.GetObjectAsync(request, ct).ConfigureAwait(false);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        var request = new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = storagePath,
        };

        await _client.DeleteObjectAsync(request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
