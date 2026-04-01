using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Core;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests.Endpoints;

public sealed class GdprEndpointTests
{
    [Fact]
    public void GdprExportRequest_ShouldHaveContactId()
    {
        var req = new GdprExportRequest("contact-123");
        req.ContactId.Should().Be("contact-123");
    }

    [Fact]
    public void GdprPurgeRequest_ShouldHaveContactIdAndReason()
    {
        var req = new GdprPurgeRequest("contact-123", "Subject erasure request");
        req.ContactId.Should().Be("contact-123");
        req.Reason.Should().Be("Subject erasure request");
    }

    [Fact]
    public void RetentionPolicyDto_ShouldSupportNullFields()
    {
        var dto = new RetentionPolicyDto("tenant-1", 90, null, null, null);
        dto.TenantId.Should().Be("tenant-1");
        dto.ConversationRetentionDays.Should().Be(90);
        dto.AuthEventRetentionDays.Should().BeNull();
        dto.AuditRetentionDays.Should().BeNull();
        dto.UsageRecordRetentionDays.Should().BeNull();
    }

    [Fact]
    public void UpdateRetentionPolicyRequest_ShouldSupportPartialUpdate()
    {
        var req = new UpdateRetentionPolicyRequest(30, null, 365, null);
        req.ConversationRetentionDays.Should().Be(30);
        req.AuthEventRetentionDays.Should().BeNull();
        req.AuditRetentionDays.Should().Be(365);
    }

    [Fact]
    public void PurgeEntry_ShouldMapAllFields()
    {
        var entry = new PurgeEntry
        {
            PurgeId = "purge-1",
            TenantId = "tenant-1",
            SubjectType = "contact",
            SubjectId = "contact-1",
            PerformedBy = "admin",
            Reason = "GDPR request",
            EntitiesDeleted = new Dictionary<string, int>
            {
                ["messages"] = 42,
                ["conversations"] = 5,
                ["contact"] = 1,
            },
            PurgedAt = DateTimeOffset.UtcNow,
        };

        entry.PurgeId.Should().Be("purge-1");
        entry.EntitiesDeleted.Should().HaveCount(3);
        entry.EntitiesDeleted["messages"].Should().Be(42);
    }
}
