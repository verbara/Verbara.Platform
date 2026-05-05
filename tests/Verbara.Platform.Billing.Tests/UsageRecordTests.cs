using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Billing.Tests;

public class UsageRecordTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    [Fact]
    public void UsageRecord_ShouldHoldAllProperties()
    {
        var id = EntityId.New();
        var now = DateTimeOffset.UtcNow;
        var meta = new Dictionary<string, string> { ["campaign"] = "c1" };

        var record = new UsageRecord
        {
            RecordId = id,
            TenantId = Tenant1,
            UsageType = UsageType.VoiceInbound,
            Quantity = 5.5m,
            Unit = UsageUnit.Minutes,
            Channel = "voice",
            ReferenceId = "call-123",
            RecordedAt = now,
            Metadata = meta,
        };

        record.RecordId.Should().Be(id);
        record.TenantId.Should().Be(Tenant1);
        record.UsageType.Should().Be(UsageType.VoiceInbound);
        record.Quantity.Should().Be(5.5m);
        record.Unit.Should().Be(UsageUnit.Minutes);
        record.Channel.Should().Be("voice");
        record.ReferenceId.Should().Be("call-123");
        record.RecordedAt.Should().Be(now);
        record.Metadata.Should().ContainKey("campaign").WhoseValue.Should().Be("c1");
    }

    [Fact]
    public void UsageRecord_ShouldAllowNullOptionalFields()
    {
        var record = new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = Tenant1,
            UsageType = UsageType.SmsOutbound,
            Quantity = 1m,
            Unit = UsageUnit.Segments,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        record.Channel.Should().BeNull();
        record.ReferenceId.Should().BeNull();
        record.Metadata.Should().BeNull();
    }

    [Fact]
    public void UsageRecord_ShouldImplementITenantScoped()
    {
        var record = new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = Tenant1,
            UsageType = UsageType.WebChatSession,
            Quantity = 1m,
            Unit = UsageUnit.Conversations,
            RecordedAt = DateTimeOffset.UtcNow,
        };

#pragma warning disable CA1859
        ITenantScoped scoped = record;
#pragma warning restore CA1859
        scoped.TenantId.Should().Be(Tenant1);
    }
}

public class UsageSummaryTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    [Fact]
    public void UsageSummary_ShouldHoldAllProperties()
    {
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var updated = DateTimeOffset.UtcNow;

        var summary = new UsageSummary
        {
            TenantId = Tenant1,
            PeriodStart = start,
            PeriodEnd = end,
            UsageType = UsageType.VoiceInbound,
            TotalQuantity = 1234.5m,
            RecordCount = 42,
            LastUpdatedAt = updated,
        };

        summary.TenantId.Should().Be(Tenant1);
        summary.PeriodStart.Should().Be(start);
        summary.PeriodEnd.Should().Be(end);
        summary.UsageType.Should().Be(UsageType.VoiceInbound);
        summary.TotalQuantity.Should().Be(1234.5m);
        summary.RecordCount.Should().Be(42);
        summary.LastUpdatedAt.Should().Be(updated);
    }
}
