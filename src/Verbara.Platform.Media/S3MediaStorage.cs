using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Media;

/// <summary>
/// S3-compatible implementation of <see cref="IMediaStorage"/>.
/// Supports AWS S3 and MinIO (path-style addressing).
/// Storage paths are object keys of the form <c>{tenantId}/{guid}_{fileName}</c>.
/// </summary>
public sealed class S3MediaStorage : IMediaStorage, IDisposable
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps each S3 operation.
    /// Registered in Platform.Api Program.cs with circuit 5/60s + retry 3/500ms + timeout 30s.
    /// </summary>
    public const string ResiliencePolicyKey = "storage.s3";

    private readonly IAmazonS3 _client;
    private readonly string _bucketName;
    private readonly bool _ownsClient;
    private readonly ResiliencePolicy _policy;

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
    /// <param name="policy">
    /// Optional keyed <see cref="ResiliencePolicy"/>; when null a NoOp policy is used so the
    /// class remains functional without DI in tests/demos.
    /// </param>
    public S3MediaStorage(
        string bucketName,
        string serviceUrl,
        string? region = null,
        bool forcePathStyle = true,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceUrl);

        _bucketName = bucketName;

        // AWS SDK defaults to RetryMode.Legacy with MaxErrorRetry = 4. We disable the SDK's
        // built-in retry (MaxErrorRetry = 0) to avoid double-retry with ResiliencePolicy —
        // 3 policy retries × 4 SDK retries = up to 12 attempts per call, which masks circuit
        // state and doubles the effective timeout. ResiliencePolicy owns all retry semantics.
        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = forcePathStyle,
            MaxErrorRetry = 0,
            RetryMode = RequestRetryMode.Standard,
        };

        if (!string.IsNullOrWhiteSpace(region))
            config.AuthenticationRegion = region;

        _client = new AmazonS3Client(config);
        _ownsClient = true;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    /// <summary>
    /// Test-only constructor that accepts a pre-built <see cref="IAmazonS3"/> client and
    /// <see cref="ResiliencePolicy"/>, enabling unit tests to verify retry/wrap behaviour.
    /// </summary>
    internal S3MediaStorage(IAmazonS3 client, string bucketName, ResiliencePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(policy);

        _bucketName = bucketName;
        _client = client;
        _ownsClient = false;
        _policy = policy;
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

        await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                var request = new PutObjectRequest
                {
                    BucketName = _bucketName,
                    Key = key,
                    InputStream = content,
                    ContentType = contentType,
                    AutoCloseStream = false,
                };
                await _client.PutObjectAsync(request, innerCt).ConfigureAwait(false);
                return 0;
            },
            ct).ConfigureAwait(false);

        return key;
    }

    /// <inheritdoc />
    public async Task<Stream?> DownloadAsync(string storagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        return await _policy.ExecuteAsync<Stream?>(
            ResiliencePolicyKey,
            async innerCt =>
            {
                try
                {
                    var request = new GetObjectRequest
                    {
                        BucketName = _bucketName,
                        Key = storagePath,
                    };
                    var response = await _client.GetObjectAsync(request, innerCt).ConfigureAwait(false);
                    return response.ResponseStream;
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // 404 is a contract value, not a transient fault — return null inside the
                    // policy so no retry attempts are spent on missing objects.
                    return null;
                }
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                var request = new DeleteObjectRequest
                {
                    BucketName = _bucketName,
                    Key = storagePath,
                };
                await _client.DeleteObjectAsync(request, innerCt).ConfigureAwait(false);
                return 0;
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}
