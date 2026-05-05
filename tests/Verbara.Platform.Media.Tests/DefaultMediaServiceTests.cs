using Verbara.Platform.Core;
using Verbara.Platform.Media;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Media.Tests;

public class DefaultMediaServiceTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private static (DefaultMediaService Service, IMediaStorage Storage, IMediaStore Store) Build(
        Action<MediaOptions>? configure = null)
    {
        var storage = Substitute.For<IMediaStorage>();
        var store = Substitute.For<IMediaStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var options = new MediaOptions();
        configure?.Invoke(options);

        var service = new DefaultMediaService(storage, store, clock, Options.Create(options));
        return (service, storage, store);
    }

    private static MemoryStream MakeStream(int bytes = 1024)
    {
        var data = new byte[bytes];
        Random.Shared.NextBytes(data);
        return new MemoryStream(data);
    }

    [Fact]
    public async Task UploadAsync_ShouldCallStorageAndSaveMetadata()
    {
        var (service, storage, store) = Build();
        storage.UploadAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
               .Returns("/storage/path/file.jpg");
        store.SaveAsync(Arg.Any<MediaFile>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var content = MakeStream();
        var file = await service.UploadAsync(Tenant1, "photo.jpg", "image/jpeg", content, ct: CancellationToken.None);

        file.Should().NotBeNull();
        file.FileName.Should().Be("photo.jpg");
        file.ContentType.Should().Be("image/jpeg");
        file.StoragePath.Should().Be("/storage/path/file.jpg");
        file.TenantId.Should().Be(Tenant1);
        file.UploadedAt.Should().Be(FixedNow);
        await store.Received(1).SaveAsync(Arg.Is<MediaFile>(f => f.FileId == file.FileId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectDisallowedContentType()
    {
        var (service, _, _) = Build();

        var act = () => service.UploadAsync(Tenant1, "file.exe", "application/octet-stream", MakeStream(), ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*application/octet-stream*");
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectFileExceedingMaxSize()
    {
        var (service, _, _) = Build(o => o.MaxFileSizeMb = 1);

        // 2 MB stream — exceeds 1 MB limit
        var content = MakeStream(2 * 1024 * 1024);
        content.Position = 0;

        var act = () => service.UploadAsync(Tenant1, "big.jpg", "image/jpeg", content, ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UploadAsync_ShouldSetConversationId_WhenProvided()
    {
        var (service, storage, store) = Build();
        storage.UploadAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
               .Returns("/path");
        store.SaveAsync(Arg.Any<MediaFile>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var convId = EntityId.New();
        var file = await service.UploadAsync(Tenant1, "photo.jpg", "image/jpeg", MakeStream(), convId, ct: CancellationToken.None);

        file.ConversationId.Should().Be(convId);
    }

    [Fact]
    public async Task UploadAsync_ShouldSetUploadedBy_WhenProvided()
    {
        var (service, storage, store) = Build();
        storage.UploadAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
               .Returns("/path");
        store.SaveAsync(Arg.Any<MediaFile>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var file = await service.UploadAsync(Tenant1, "photo.jpg", "image/jpeg", MakeStream(), uploadedBy: "user-99", ct: CancellationToken.None);

        file.UploadedBy.Should().Be("user-99");
    }

    [Fact]
    public async Task DownloadAsync_ShouldReturnStream_WhenFileExists()
    {
        var (service, storage, store) = Build();
        var fileId = EntityId.New();
        var storedFile = new MediaFile
        {
            FileId = fileId,
            TenantId = Tenant1,
            FileName = "photo.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 512,
            StoragePath = "/stored/photo.jpg",
            UploadedAt = FixedNow,
        };
        store.GetByIdAsync(Tenant1, fileId, Arg.Any<CancellationToken>()).Returns(storedFile);
        var expectedStream = new MemoryStream([1, 2, 3]);
        storage.DownloadAsync("/stored/photo.jpg", Arg.Any<CancellationToken>()).Returns<Stream?>(expectedStream);

        var result = await service.DownloadAsync(Tenant1, fileId, CancellationToken.None);

        result.Should().BeSameAs(expectedStream);
    }

    [Fact]
    public async Task DownloadAsync_ShouldReturnNull_WhenFileMetadataNotFound()
    {
        var (service, _, store) = Build();
        store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
             .Returns((MediaFile?)null);

        var result = await service.DownloadAsync(Tenant1, EntityId.New(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UploadAsync_ShouldAcceptAllDefaultAllowedContentTypes()
    {
        var allowedTypes = new[]
        {
            "image/jpeg", "image/png", "image/gif",
            "audio/mpeg", "audio/ogg", "video/mp4", "application/pdf",
        };

        foreach (var ct in allowedTypes)
        {
            var (service, storage, store) = Build();
            storage.UploadAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                   .Returns("/path");
            store.SaveAsync(Arg.Any<MediaFile>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

            var act = () => service.UploadAsync(Tenant1, "file", ct, MakeStream(), ct: CancellationToken.None);
            await act.Should().NotThrowAsync($"'{ct}' should be allowed");
        }
    }

    [Fact]
    public async Task UploadAsync_ShouldGenerateUniqueFileIds()
    {
        var capturedIds = new List<EntityId>();

        var storage = Substitute.For<IMediaStorage>();
        storage.UploadAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
               .Returns("/path");
        var store = Substitute.For<IMediaStore>();
        store.SaveAsync(Arg.Do<MediaFile>(f => capturedIds.Add(f.FileId)), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultMediaService(storage, store, clock, Options.Create(new MediaOptions()));

        await service.UploadAsync(Tenant1, "a.jpg", "image/jpeg", MakeStream(), ct: CancellationToken.None);
        await service.UploadAsync(Tenant1, "b.jpg", "image/jpeg", MakeStream(), ct: CancellationToken.None);

        capturedIds.Should().HaveCount(2);
        capturedIds[0].Should().NotBe(capturedIds[1]);
    }
}

public class MediaOptionsTests
{
    [Fact]
    public void MediaOptions_ShouldHaveDefaults()
    {
        var options = new MediaOptions();

        options.BasePath.Should().Be("media");
        options.MaxFileSizeMb.Should().Be(25);
        options.AllowedContentTypes.Should().HaveCount(7);
        options.AllowedContentTypes.Should().Contain("image/jpeg");
        options.AllowedContentTypes.Should().Contain("application/pdf");
    }
}
