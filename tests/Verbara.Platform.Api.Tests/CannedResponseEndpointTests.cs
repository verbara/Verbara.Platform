using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Api.Tests;

public class CannedResponseEndpointTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");

    private static CannedResponse MakeResponse(TenantId tenantId, string shortcut,
        string title = "Title", string body = "Body", string? category = null) =>
        new()
        {
            ResponseId = EntityId.New(),
            TenantId = tenantId,
            Shortcut = shortcut,
            Title = title,
            Body = body,
            Category = category,
            Tags = [],
            CreatedBy = "admin",
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task SaveAndGet_ShouldReturnCreatedResponse()
    {
        var store = new InMemoryCannedResponseStore();
        var response = MakeResponse(Tenant1, "/greeting", "Hello", "Hello {{name}}!");

        await store.SaveAsync(response, CancellationToken.None);
        var result = await store.GetByIdAsync(Tenant1, response.ResponseId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Shortcut.Should().Be("/greeting");
        result.Body.Should().Be("Hello {{name}}!");
    }

    [Fact]
    public async Task Search_ShouldMatchByShortcut()
    {
        var store = new InMemoryCannedResponseStore();
        await store.SaveAsync(MakeResponse(Tenant1, "/greeting", "Greeting"), CancellationToken.None);
        await store.SaveAsync(MakeResponse(Tenant1, "/closing", "Closing"), CancellationToken.None);

        var results = await store.SearchAsync(Tenant1, "greet", CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Shortcut.Should().Be("/greeting");
    }

    [Fact]
    public async Task Search_ShouldMatchByCategory()
    {
        var store = new InMemoryCannedResponseStore();
        await store.SaveAsync(MakeResponse(Tenant1, "/a", category: "FAQ"), CancellationToken.None);
        await store.SaveAsync(MakeResponse(Tenant1, "/b", category: "Sales"), CancellationToken.None);

        var results = await store.SearchAsync(Tenant1, "faq", CancellationToken.None);

        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListByTenant_ShouldOnlyReturnOwnTenant()
    {
        var store = new InMemoryCannedResponseStore();
        await store.SaveAsync(MakeResponse(Tenant1, "/a"), CancellationToken.None);
        await store.SaveAsync(MakeResponse(Tenant2, "/b"), CancellationToken.None);

        var results = await store.ListByTenantAsync(Tenant1, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Shortcut.Should().Be("/a");
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
    public async Task Update_ShouldModifyExistingResponse()
    {
        var store = new InMemoryCannedResponseStore();
        var response = MakeResponse(Tenant1, "/greeting", "Hello", "Hello!");
        await store.SaveAsync(response, CancellationToken.None);

        response.Title = "Updated Hello";
        response.Body = "Updated body";
        response.UpdatedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(response, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant1, response.ResponseId, CancellationToken.None);
        result!.Title.Should().Be("Updated Hello");
        result.Body.Should().Be("Updated body");
    }
}
