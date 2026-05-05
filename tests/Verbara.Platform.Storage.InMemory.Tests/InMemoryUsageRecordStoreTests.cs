using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryUsageRecordStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");
    private static readonly DateTimeOffset BaseTime = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static UsageRecord MakeRecord(
        TenantId? tenantId = null,
        UsageType type = UsageType.VoiceInbound,
        decimal quantity = 5m,
        UsageUnit unit = UsageUnit.Minutes,
        DateTimeOffset? recordedAt = null) => new()
    {
        RecordId = EntityId.New(),
        TenantId = tenantId ?? Tenant1,
        UsageType = type,
        Quantity = quantity,
        Unit = unit,
        RecordedAt = recordedAt ?? BaseTime,
    };

    [Fact]
    public async Task SaveAsync_ShouldPersistRecord()
    {
        var store = new InMemoryUsageRecordStore();
        var record = MakeRecord();

        await store.SaveAsync(record, CancellationToken.None);

        var summary = await store.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);
        summary.Should().NotBeNull();
        summary!.TotalQuantity.Should().Be(5m);
        summary.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task SaveBatchAsync_ShouldPersistAllRecords()
    {
        var store = new InMemoryUsageRecordStore();
        var records = new List<UsageRecord>
        {
            MakeRecord(quantity: 3m),
            MakeRecord(quantity: 7m),
        };

        await store.SaveBatchAsync(records, CancellationToken.None);

        var summary = await store.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);
        summary.Should().NotBeNull();
        summary!.TotalQuantity.Should().Be(10m);
        summary.RecordCount.Should().Be(2);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldGroupByUsageType()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(type: UsageType.VoiceInbound, quantity: 10m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(type: UsageType.VoiceInbound, quantity: 5m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(type: UsageType.SmsOutbound, quantity: 3m, unit: UsageUnit.Segments), CancellationToken.None);

        var summaries = await store.GetSummaryAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        summaries.Should().HaveCount(2);
        var voice = summaries.First(s => s.UsageType == UsageType.VoiceInbound);
        voice.TotalQuantity.Should().Be(15m);
        voice.RecordCount.Should().Be(2);
        var sms = summaries.First(s => s.UsageType == UsageType.SmsOutbound);
        sms.TotalQuantity.Should().Be(3m);
        sms.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldFilterByDateRange()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddDays(-1)), CancellationToken.None);
        await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddDays(5), quantity: 8m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddMonths(2)), CancellationToken.None);

        var summaries = await store.GetSummaryAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        summaries.Should().HaveCount(1);
        summaries[0].TotalQuantity.Should().Be(8m);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldReturnEmpty_WhenNoRecords()
    {
        var store = new InMemoryUsageRecordStore();

        var summaries = await store.GetSummaryAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSummaryByTypeAsync_ShouldReturnNull_WhenNoRecordsForType()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(type: UsageType.SmsOutbound, unit: UsageUnit.Segments), CancellationToken.None);

        var summary = await store.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        summary.Should().BeNull();
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldIsolateTenants()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(tenantId: Tenant1, quantity: 10m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(tenantId: Tenant2, quantity: 20m), CancellationToken.None);

        var s1 = await store.GetSummaryAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);
        var s2 = await store.GetSummaryAsync(Tenant2, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        s1.Should().HaveCount(1);
        s1[0].TotalQuantity.Should().Be(10m);
        s2.Should().HaveCount(1);
        s2[0].TotalQuantity.Should().Be(20m);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldSetPeriodBounds()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(), CancellationToken.None);
        var from = BaseTime;
        var to = BaseTime.AddMonths(1);

        var summaries = await store.GetSummaryAsync(Tenant1, from, to, CancellationToken.None);

        summaries[0].PeriodStart.Should().Be(from);
        summaries[0].PeriodEnd.Should().Be(to);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnPaginatedRecords()
    {
        var store = new InMemoryUsageRecordStore();
        for (int i = 0; i < 5; i++)
            await store.SaveAsync(MakeRecord(quantity: i + 1, recordedAt: BaseTime.AddHours(i)), CancellationToken.None);

        var page1 = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), null, 1, 3, CancellationToken.None);
        var page2 = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), null, 2, 3, CancellationToken.None);

        page1.Should().HaveCount(3);
        page2.Should().HaveCount(2);
        // Ordered by RecordedAt DESC, so first page has most recent
        page1[0].Quantity.Should().Be(5m);
        page1[2].Quantity.Should().Be(3m);
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByType()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(type: UsageType.VoiceInbound, quantity: 10m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(type: UsageType.SmsOutbound, quantity: 3m, unit: UsageUnit.Segments), CancellationToken.None);
        await store.SaveAsync(MakeRecord(type: UsageType.VoiceInbound, quantity: 5m, recordedAt: BaseTime.AddHours(1)), CancellationToken.None);

        var result = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), UsageType.VoiceInbound, 1, 50, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(r => r.UsageType == UsageType.VoiceInbound);
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByDateRange()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddDays(-1)), CancellationToken.None);
        await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddDays(5), quantity: 8m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddMonths(2)), CancellationToken.None);

        var result = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), null, 1, 50, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Quantity.Should().Be(8m);
    }

    [Fact]
    public async Task ListAsync_ShouldIsolateTenants()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(tenantId: Tenant1, quantity: 10m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(tenantId: Tenant2, quantity: 20m), CancellationToken.None);

        var result = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), null, 1, 50, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Quantity.Should().Be(10m);
    }
}
