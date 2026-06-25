using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Round-trips <see cref="PostgresUsageRecordStore"/> against a real Postgres DB via
/// Testcontainers so the JSONB-based per-direction token breakdown aggregation
/// (<c>GetAiTokenBreakdownAsync</c>) — split vs. unsplit (incl. NULL-metadata) buckets —
/// is exercised end-to-end against the real <c>usage_records</c> schema.
/// </summary>
public sealed class PostgresUsageRecordStoreTests : IClassFixture<UsageRecordStoreFixture>, IAsyncLifetime
{
    private readonly UsageRecordStoreFixture _fixture;
    private readonly PostgresUsageRecordStore _store;
    private static readonly TenantId Tenant1 = new("acme-usage");
    private static readonly DateTimeOffset BaseTime = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    public PostgresUsageRecordStoreTests(UsageRecordStoreFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresUsageRecordStore(_fixture.DataSource);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static UsageRecord MakeAi(
        decimal quantity,
        Dictionary<string, string>? metadata,
        DateTimeOffset? recordedAt = null,
        TenantId? tenantId = null,
        UsageType type = UsageType.AiAnalysis) => new()
    {
        RecordId = EntityId.New(),
        TenantId = tenantId ?? Tenant1,
        UsageType = type,
        Quantity = quantity,
        Unit = UsageUnit.Tokens,
        RecordedAt = recordedAt ?? BaseTime,
        Metadata = metadata,
    };

    [Fact]
    public async Task GetAiTokenBreakdownAsync_ShouldSumSplitAndUnsplitBuckets_WhenMixedMetadata()
    {
        // Split record: both keys present.
        await _store.SaveAsync(MakeAi(400m, new Dictionary<string, string>
        {
            ["inputTokens"] = "300", ["outputTokens"] = "100", ["model"] = "gpt-4o",
        }), CancellationToken.None);
        // Unsplit: NULL metadata -> counted via Quantity, never dropped.
        await _store.SaveAsync(MakeAi(500m, metadata: null), CancellationToken.None);
        // Unsplit: metadata present but missing outputTokens -> Quantity bucket.
        await _store.SaveAsync(MakeAi(70m, new Dictionary<string, string>
        {
            ["inputTokens"] = "70",
        }), CancellationToken.None);
        // Different type -> excluded.
        await _store.SaveAsync(MakeAi(999m, metadata: null, type: UsageType.VoiceInbound), CancellationToken.None);
        // AiAnalysis but out of range -> excluded.
        await _store.SaveAsync(MakeAi(999m, new Dictionary<string, string>
        {
            ["inputTokens"] = "1", ["outputTokens"] = "1",
        }, recordedAt: BaseTime.AddMonths(2)), CancellationToken.None);
        // Different tenant -> excluded.
        await _store.SaveAsync(MakeAi(999m, new Dictionary<string, string>
        {
            ["inputTokens"] = "5", ["outputTokens"] = "5",
        }, tenantId: new TenantId("other")), CancellationToken.None);

        var breakdown = await _store.GetAiTokenBreakdownAsync(
            Tenant1, UsageType.AiAnalysis, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        breakdown.InputTokens.Should().Be(300m);
        breakdown.OutputTokens.Should().Be(100m);
        breakdown.UnsplitTokens.Should().Be(570m); // 500 (NULL metadata) + 70 (missing outputTokens)
    }

    [Fact]
    public async Task GetAiTokenBreakdownAsync_ShouldReturnZeros_WhenNoRecords()
    {
        var breakdown = await _store.GetAiTokenBreakdownAsync(
            Tenant1, UsageType.AiAnalysis, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        breakdown.InputTokens.Should().Be(0m);
        breakdown.OutputTokens.Should().Be(0m);
        breakdown.UnsplitTokens.Should().Be(0m);
    }

    [Fact]
    public async Task GetAiTokenBreakdownAsync_ShouldCountNullMetadataAsUnsplit_WhenAllNull()
    {
        await _store.SaveAsync(MakeAi(120m, metadata: null), CancellationToken.None);
        await _store.SaveAsync(MakeAi(80m, metadata: null), CancellationToken.None);

        var breakdown = await _store.GetAiTokenBreakdownAsync(
            Tenant1, UsageType.AiAnalysis, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        breakdown.InputTokens.Should().Be(0m);
        breakdown.OutputTokens.Should().Be(0m);
        breakdown.UnsplitTokens.Should().Be(200m);
    }
}
