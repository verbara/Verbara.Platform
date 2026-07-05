using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// audit-trail-integrity-fixes (fix 2): <c>PreviewUserPurgeAsync</c> must report the REAL number
/// of audit-trail rows attributable to the user — not a hard-coded 0. "Attributable to the user"
/// is the rows where <see cref="AuditEntry.ActorId"/> is the user (the linkage
/// <see cref="IAuditStore.CountByActorAsync"/> counts by).
/// </summary>
public sealed class GdprPurgePreviewTests
{
    private static readonly TenantId Tenant = new("tenant-preview");

    private static async Task<IAuditStore> SeedAuditRowsAsync(string tenantId, string userId, int count, int otherCount = 0)
    {
        var store = new InMemoryAuditStore();
        var auditService = new DefaultAuditService(store, new SystemClock());

        for (var i = 0; i < count; i++)
        {
            await auditService.RecordAsync(
                new TenantId(tenantId), category: "config", action: $"test.action.{i}", severity: "info",
                actorId: userId, actorType: "user",
                targetId: $"target-{i}", targetType: "TestEntity",
                ct: CancellationToken.None);
        }

        // Rows attributed to a DIFFERENT actor must NOT be counted.
        for (var i = 0; i < otherCount; i++)
        {
            await auditService.RecordAsync(
                new TenantId(tenantId), category: "config", action: $"other.action.{i}", severity: "info",
                actorId: "someone-else", actorType: "user",
                targetId: $"other-target-{i}", targetType: "TestEntity",
                ct: CancellationToken.None);
        }

        return store;
    }

    [Fact]
    public async Task PreviewUserPurgeAsync_ShouldReportRealAuditTrailCount_WhenAuditRowsExist()
    {
        var auditStore = await SeedAuditRowsAsync(Tenant.Value, "user-42", count: 12, otherCount: 5);
        var authEventStore = new InMemoryAuthEventStore();
        var service = new GdprPurgeService(
            new InMemoryContactStore(),
            new InMemoryConversationStore(),
            new InMemoryMessageStore(),
            authEventStore,
            new InMemoryUserStore(),
            new InMemoryPurgeLogStore(),
            auditStore);

        var preview = await service.PreviewUserPurgeAsync(Tenant.Value, "user-42", CancellationToken.None);

        preview.AuditTrailCount.Should().Be(12, "only rows attributed to THIS user count, not the other actor's 5 rows");
    }

    [Fact]
    public async Task PreviewUserPurgeAsync_ShouldReportZero_WhenUserHasNoAuditRows()
    {
        var auditStore = new InMemoryAuditStore();
        var service = new GdprPurgeService(
            new InMemoryContactStore(),
            new InMemoryConversationStore(),
            new InMemoryMessageStore(),
            new InMemoryAuthEventStore(),
            new InMemoryUserStore(),
            new InMemoryPurgeLogStore(),
            auditStore);

        var preview = await service.PreviewUserPurgeAsync(Tenant.Value, "user-with-no-activity", CancellationToken.None);

        preview.AuditTrailCount.Should().Be(0);
    }

    [Fact]
    public async Task PreviewUserPurgeAsync_ShouldScopeCountByTenant_WhenSameUserIdExistsInAnotherTenant()
    {
        var auditStore = new InMemoryAuditStore();
        var auditService = new DefaultAuditService(auditStore, new SystemClock());

        // Same user id, different tenants — only the requested tenant's rows must count.
        await auditService.RecordAsync(
            Tenant, category: "config", action: "test.action", severity: "info",
            actorId: "shared-user-id", actorType: "user", ct: CancellationToken.None);
        await auditService.RecordAsync(
            new TenantId("other-tenant"), category: "config", action: "test.action", severity: "info",
            actorId: "shared-user-id", actorType: "user", ct: CancellationToken.None);

        var service = new GdprPurgeService(
            new InMemoryContactStore(),
            new InMemoryConversationStore(),
            new InMemoryMessageStore(),
            new InMemoryAuthEventStore(),
            new InMemoryUserStore(),
            new InMemoryPurgeLogStore(),
            auditStore);

        var preview = await service.PreviewUserPurgeAsync(Tenant.Value, "shared-user-id", CancellationToken.None);

        preview.AuditTrailCount.Should().Be(1);
    }
}
