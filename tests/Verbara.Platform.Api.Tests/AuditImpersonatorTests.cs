using Verbara.Platform.Audit;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests;

public sealed class AuditImpersonatorTests
{
    [Fact]
    public void AuditEntry_ShouldAcceptImpersonatorId_WhenProvided()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = new TenantId("tenant-001"),
            Action = "user.updated",
            ImpersonatorId = "admin-user-42",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.ImpersonatorId.Should().Be("admin-user-42");
    }

    [Fact]
    public void AuditEntry_ShouldDefaultImpersonatorIdToNull_WhenNotProvided()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = new TenantId("tenant-001"),
            Action = "role.assigned",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.ImpersonatorId.Should().BeNull();
    }

    [Fact]
    public void AuditEntry_ShouldIncludeImpersonatorIdInMetadata_WhenBothAreSet()
    {
        var metadata = new Dictionary<string, string>
        {
            ["mode"] = "read_only",
        };

        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = new TenantId("tenant-002"),
            Action = "conversation.viewed",
            ImpersonatorId = "platform-admin-7",
            Metadata = metadata,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.ImpersonatorId.Should().Be("platform-admin-7");
        entry.Metadata.Should().ContainKey("mode").WhoseValue.Should().Be("read_only");
    }
}
