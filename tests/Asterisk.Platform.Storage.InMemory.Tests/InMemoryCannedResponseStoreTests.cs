using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public class InMemoryCannedResponseStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");

    private static CannedResponse MakeResponse(TenantId tenantId, string shortcut, string title = "Title",
        string body = "Body", string? category = null, string[]? tags = null) =>
        new()
        {
            ResponseId = EntityId.New(),
            TenantId = tenantId,
            Shortcut = shortcut,
            Title = title,
            Body = body,
            Category = category,
            Tags = tags ?? [],
            CreatedBy = "admin",
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task SaveAndGet_ShouldRoundTrip()
    {
        var store = new InMemoryCannedResponseStore();
        var response = MakeResponse(Tenant1, "/greeting", "Hello", "Hello {{name}}!");

        await store.SaveAsync(response, CancellationToken.None);
        var result = await store.GetByIdAsync(Tenant1, response.ResponseId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Shortcut.Should().Be("/greeting");
        result.Title.Should().Be("Hello");
        result.Body.Should().Be("Hello {{name}}!");
    }

    [Fact]
    public async Task ListByTenant_ShouldReturnAllForTenant()
    {
        var store = new InMemoryCannedResponseStore();
        await store.SaveAsync(MakeResponse(Tenant1, "/a"), CancellationToken.None);
        await store.SaveAsync(MakeResponse(Tenant1, "/b"), CancellationToken.None);
        await store.SaveAsync(MakeResponse(Tenant2, "/c"), CancellationToken.None);

        var results = await store.ListByTenantAsync(Tenant1, CancellationToken.None);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_ShouldMatchShortcut()
    {
        var store = new InMemoryCannedResponseStore();
        await store.SaveAsync(MakeResponse(Tenant1, "/greeting"), CancellationToken.None);
        await store.SaveAsync(MakeResponse(Tenant1, "/closing"), CancellationToken.None);

        var results = await store.SearchAsync(Tenant1, "greet", CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Shortcut.Should().Be("/greeting");
    }

    [Fact]
    public async Task Search_ShouldMatchCategory()
    {
        var store = new InMemoryCannedResponseStore();
        await store.SaveAsync(MakeResponse(Tenant1, "/a", category: "FAQ"), CancellationToken.None);
        await store.SaveAsync(MakeResponse(Tenant1, "/b", category: "Greetings"), CancellationToken.None);

        var results = await store.SearchAsync(Tenant1, "faq", CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Category.Should().Be("FAQ");
    }

    [Fact]
    public async Task Delete_ShouldRemoveResponse()
    {
        var store = new InMemoryCannedResponseStore();
        var response = MakeResponse(Tenant1, "/temp");
        await store.SaveAsync(response, CancellationToken.None);

        await store.DeleteAsync(Tenant1, response.ResponseId, CancellationToken.None);
        var result = await store.GetByIdAsync(Tenant1, response.ResponseId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetById_ShouldRespectTenantIsolation()
    {
        var store = new InMemoryCannedResponseStore();
        var response = MakeResponse(Tenant1, "/secret");
        await store.SaveAsync(response, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant2, response.ResponseId, CancellationToken.None);

        result.Should().BeNull();
    }
}
