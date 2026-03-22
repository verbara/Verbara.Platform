using Asterisk.Platform.Core;
using Asterisk.Platform.Media;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class InMemoryMediaStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");

    private static MediaFile MakeFile(TenantId? tenantId = null, EntityId? conversationId = null) => new()
    {
        FileId = EntityId.New(),
        TenantId = tenantId ?? Tenant1,
        FileName = "photo.jpg",
        ContentType = "image/jpeg",
        SizeBytes = 1024,
        StoragePath = "/tmp/photo.jpg",
        ConversationId = conversationId,
        UploadedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task GetByIdAsync_ShouldReturnFile_WhenItExists()
    {
        var store = new InMemoryMediaStore();
        var file = MakeFile();
        await store.SaveAsync(file, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant1, file.FileId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.FileId.Should().Be(file.FileId);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = new InMemoryMediaStore();

        var result = await store.GetByIdAsync(Tenant1, EntityId.New(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveFile()
    {
        var store = new InMemoryMediaStore();
        var file = MakeFile();
        await store.SaveAsync(file, CancellationToken.None);

        await store.DeleteAsync(Tenant1, file.FileId, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant1, file.FileId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByConversationAsync_ShouldReturnFilesForConversation()
    {
        var store = new InMemoryMediaStore();
        var convId = EntityId.New();
        var file1 = MakeFile(conversationId: convId);
        var file2 = MakeFile(conversationId: convId);
        var unrelated = MakeFile(conversationId: EntityId.New());
        await store.SaveAsync(file1, CancellationToken.None);
        await store.SaveAsync(file2, CancellationToken.None);
        await store.SaveAsync(unrelated, CancellationToken.None);

        var result = await store.GetByConversationAsync(Tenant1, convId, CancellationToken.None);

        result.Should().HaveCount(2);
        result.All(f => f.ConversationId == convId).Should().BeTrue();
    }

    [Fact]
    public async Task GetByConversationAsync_ShouldIsolateTenants()
    {
        var store = new InMemoryMediaStore();
        var convId = EntityId.New();
        var t1File = MakeFile(tenantId: Tenant1, conversationId: convId);
        var t2File = MakeFile(tenantId: Tenant2, conversationId: convId);
        await store.SaveAsync(t1File, CancellationToken.None);
        await store.SaveAsync(t2File, CancellationToken.None);

        var result = await store.GetByConversationAsync(Tenant1, convId, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].TenantId.Should().Be(Tenant1);
    }
}
