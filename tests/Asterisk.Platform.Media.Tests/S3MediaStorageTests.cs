using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Asterisk.Platform.Core;
using Asterisk.Platform.Media;
using Asterisk.Sdk.Resilience;
using NSubstitute.ExceptionExtensions;

namespace Asterisk.Platform.Media.Tests;

public sealed class S3MediaStorageTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private const string BucketName = "test-bucket";

    private static ResiliencePolicy BuildPolicy() => new ResiliencePolicyBuilder()
        .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1))
        .Build();

    private static MemoryStream NewStream(int bytes = 16)
    {
        var buf = new byte[bytes];
        Random.Shared.NextBytes(buf);
        return new MemoryStream(buf);
    }

    [Fact]
    public async Task UploadAsync_ShouldInvokePutObjectOnce_WhenUploadSucceeds()
    {
        var client = Substitute.For<IAmazonS3>();
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        using var sut = new S3MediaStorage(client, BucketName, BuildPolicy());

        var key = await sut.UploadAsync(Tenant, "file.txt", "text/plain", NewStream(), CancellationToken.None);

        key.Should().StartWith("tenant-1/");
        await client.Received(1).PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransient()
    {
        var client = Substitute.For<IAmazonS3>();
        var attempts = 0;
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new AmazonS3Exception("transient S3 blip") { StatusCode = HttpStatusCode.ServiceUnavailable };
                return Task.FromResult(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });
            });

        using var sut = new S3MediaStorage(client, BucketName, BuildPolicy());

        await sut.UploadAsync(Tenant, "file.txt", "text/plain", NewStream(), CancellationToken.None);

        attempts.Should().Be(2, "first PutObject threw transient, policy retried and succeeded");
    }

    [Fact]
    public async Task DownloadAsync_ShouldReturnNullWithoutRetry_WhenObjectMissing()
    {
        var client = Substitute.For<IAmazonS3>();
        client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Throws(new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound });

        using var sut = new S3MediaStorage(client, BucketName, BuildPolicy());

        var stream = await sut.DownloadAsync("tenant-1/missing", CancellationToken.None);

        stream.Should().BeNull();
        // 404 is a contract value — policy wraps the whole try/catch block; should not retry.
        await client.Received(1).GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadAsync_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransient()
    {
        var client = Substitute.For<IAmazonS3>();
        var attempts = 0;
        client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new AmazonS3Exception("transient S3 blip") { StatusCode = HttpStatusCode.ServiceUnavailable };
                return Task.FromResult(new GetObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("hello")),
                });
            });

        using var sut = new S3MediaStorage(client, BucketName, BuildPolicy());

        var result = await sut.DownloadAsync("tenant-1/file.txt", CancellationToken.None);

        result.Should().NotBeNull();
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task DeleteAsync_ShouldInvokeDeleteOnce_WhenSucceeds()
    {
        var client = Substitute.For<IAmazonS3>();
        client.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.NoContent });

        using var sut = new S3MediaStorage(client, BucketName, BuildPolicy());

        await sut.DeleteAsync("tenant-1/file.txt", CancellationToken.None);

        await client.Received(1).DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        S3MediaStorage.ResiliencePolicyKey.Should().Be("storage.s3");
    }
}
