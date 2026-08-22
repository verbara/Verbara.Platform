using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Billing.Tests;

public class DefaultMeteringServiceTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset FixedNow = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);

    private static (DefaultMeteringService Service, IUsageRecordStore Store, IClock Clock) Build()
    {
        var store = Substitute.For<IUsageRecordStore>();
        store.SaveAsync(Arg.Any<UsageRecord>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        store.SaveBatchAsync(Arg.Any<IReadOnlyList<UsageRecord>>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultMeteringService(store, clock);
        return (service, store, clock);
    }

    [Fact]
    public async Task RecordUsageAsync_ShouldSaveRecord_WithCorrectFields()
    {
        var (service, store, _) = Build();

        await service.RecordUsageAsync(Tenant1, UsageType.VoiceInbound, 3.5m, UsageUnit.Minutes, "voice", "call-1", CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<UsageRecord>(r => r != null &&
                r.TenantId == Tenant1 &&
                r.UsageType == UsageType.VoiceInbound &&
                r.Quantity == 3.5m &&
                r.Unit == UsageUnit.Minutes &&
                r.Channel == "voice" &&
                r.ReferenceId == "call-1" &&
                r.RecordedAt == FixedNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordUsageAsync_ShouldGenerateUniqueRecordId()
    {
        var capturedIds = new List<string>();
        var store = Substitute.For<IUsageRecordStore>();
        store.SaveAsync(Arg.Do<UsageRecord>(r => capturedIds.Add(r.RecordId.Value)), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);
        var service = new DefaultMeteringService(store, clock);

        await service.RecordUsageAsync(Tenant1, UsageType.SmsOutbound, 1m, UsageUnit.Segments, null, null, CancellationToken.None);
        await service.RecordUsageAsync(Tenant1, UsageType.SmsOutbound, 1m, UsageUnit.Segments, null, null, CancellationToken.None);

        capturedIds.Should().HaveCount(2);
        capturedIds[0].Should().NotBe(capturedIds[1]);
    }

    [Fact]
    public async Task RecordBatchAsync_ShouldDelegateToStore()
    {
        var (service, store, _) = Build();
        var records = new List<UsageRecord>
        {
            new()
            {
                RecordId = EntityId.New(),
                TenantId = Tenant1,
                UsageType = UsageType.SmsInbound,
                Quantity = 1m,
                Unit = UsageUnit.Segments,
                RecordedAt = FixedNow,
            },
        };

        await service.RecordBatchAsync(records, CancellationToken.None);

        await store.Received(1).SaveBatchAsync(records, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCurrentPeriodSummaryAsync_ShouldQueryCurrentMonth()
    {
        var (service, store, _) = Build();
        var expectedStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var expectedEnd = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var summaries = new List<UsageSummary>();
        store.GetSummaryAsync(Tenant1, expectedStart, expectedEnd, Arg.Any<CancellationToken>())
             .Returns(summaries);

        var result = await service.GetCurrentPeriodSummaryAsync(Tenant1, CancellationToken.None);

        result.Should().BeSameAs(summaries);
        await store.Received(1).GetSummaryAsync(Tenant1, expectedStart, expectedEnd, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordUsageAsync_ShouldAllowNullOptionalFields()
    {
        var (service, store, _) = Build();

        await service.RecordUsageAsync(Tenant1, UsageType.WebChatSession, 1m, UsageUnit.Conversations, null, null, CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<UsageRecord>(r => r != null && r.Channel == null && r.ReferenceId == null),
            Arg.Any<CancellationToken>());
    }
}
