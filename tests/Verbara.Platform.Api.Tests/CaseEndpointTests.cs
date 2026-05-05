using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Api.Tests;

public class CaseEndpointTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");

    private static Case MakeCase(TenantId tenantId, CaseStatus status = CaseStatus.Open,
        CasePriority priority = CasePriority.Normal) =>
        new()
        {
            CaseId = EntityId.New(),
            TenantId = tenantId,
            CaseNumber = $"CASE-{Random.Shared.Next(10000000, 99999999)}",
            Subject = "Test case",
            Priority = priority,
            Status = status,
            ContactId = EntityId.New(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task SaveAndGetById_ShouldReturnCase()
    {
        var store = new InMemoryCaseStore();
        var c = MakeCase(Tenant1);

        await store.SaveAsync(c, CancellationToken.None);
        var result = await store.GetByIdAsync(Tenant1, c.CaseId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Subject.Should().Be("Test case");
        result.Status.Should().Be(CaseStatus.Open);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnOnlyTenantCases()
    {
        var store = new InMemoryCaseStore();
        await store.SaveAsync(MakeCase(Tenant1), CancellationToken.None);
        await store.SaveAsync(MakeCase(Tenant1), CancellationToken.None);
        await store.SaveAsync(MakeCase(Tenant2), CancellationToken.None);

        var result = await store.ListAsync(Tenant1, new PagedQuery { Page = 1, PageSize = 25 }, CancellationToken.None);

        result.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateCase_ShouldModifyExisting()
    {
        var store = new InMemoryCaseStore();
        var c = MakeCase(Tenant1);
        await store.SaveAsync(c, CancellationToken.None);

        c.Status = CaseStatus.Resolved;
        c.Subject = "Updated subject";
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(c, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant1, c.CaseId, CancellationToken.None);
        result!.Status.Should().Be(CaseStatus.Resolved);
        result.Subject.Should().Be("Updated subject");
    }

    [Fact]
    public async Task AddConversation_ShouldLinkConversationToCase()
    {
        var store = new InMemoryCaseStore();
        var c = MakeCase(Tenant1);
        await store.SaveAsync(c, CancellationToken.None);

        var convId = EntityId.New();
        c.AddConversation(convId);
        await store.SaveAsync(c, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant1, c.CaseId, CancellationToken.None);
        result!.ConversationIds.Should().ContainSingle();
        result.ConversationIds[0].Should().Be(convId);
    }

    [Fact]
    public async Task AddConversation_ShouldNotDuplicate()
    {
        var c = MakeCase(Tenant1);
        var convId = EntityId.New();

        c.AddConversation(convId);
        c.AddConversation(convId);

        c.ConversationIds.Should().ContainSingle();
    }
}
