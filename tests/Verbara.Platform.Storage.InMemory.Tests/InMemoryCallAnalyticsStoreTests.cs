using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.CallAnalytics.Domain;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryCallAnalyticsStoreTests
{
    private const string Tenant = "tenant-1";

    private static CallAnalysisResult MakeResult(string tenantId, string sessionId) =>
        new()
        {
            SessionId = sessionId,
            TenantId = tenantId,
            AnalyzedAt = DateTimeOffset.UtcNow,
            ProcessingTime = TimeSpan.FromSeconds(1),
            Metrics = new ConversationMetrics
            {
                AgentTalkRatio = 0.5,
                TotalAgentTalk = TimeSpan.FromSeconds(30),
                TotalCallerTalk = TimeSpan.FromSeconds(30),
                LongestAgentMonologue = TimeSpan.FromSeconds(10),
                LongestCallerMonologue = TimeSpan.FromSeconds(10),
                AgentTurnCount = 4,
                CallerTurnCount = 4,
                SilenceCount = 1,
                TotalSilence = TimeSpan.FromSeconds(2),
                InterruptionCount = 0,
            },
        };

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldReturnRequestedSubset_WhenMultipleSaved()
    {
        var store = new InMemoryCallAnalyticsStore();
        await store.SaveAsync(MakeResult(Tenant, "s1"), CancellationToken.None);
        await store.SaveAsync(MakeResult(Tenant, "s2"), CancellationToken.None);
        await store.SaveAsync(MakeResult(Tenant, "s3"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync(["s1", "s3"], Tenant, CancellationToken.None);

        result.Select(r => r.SessionId).Should().BeEquivalentTo("s1", "s3");
    }

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldIsolateByTenant_WhenSameIdAcrossTenants()
    {
        var store = new InMemoryCallAnalyticsStore();
        await store.SaveAsync(MakeResult("tenant-a", "shared"), CancellationToken.None);
        await store.SaveAsync(MakeResult("tenant-b", "shared"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync(["shared"], "tenant-a", CancellationToken.None);

        result.Should().ContainSingle().Which.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldReturnEmpty_WhenEmptyCollection()
    {
        var store = new InMemoryCallAnalyticsStore();
        await store.SaveAsync(MakeResult(Tenant, "s1"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync([], Tenant, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldOmitUnknownIds_WhenSomeNotPresent()
    {
        var store = new InMemoryCallAnalyticsStore();
        await store.SaveAsync(MakeResult(Tenant, "known"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync(["known", "missing"], Tenant, CancellationToken.None);

        result.Should().ContainSingle().Which.SessionId.Should().Be("known");
    }

    [Fact]
    public async Task GetBySessionIdsAsync_ShouldReturnEachResultOnce_WhenDuplicateIdsRequested()
    {
        var store = new InMemoryCallAnalyticsStore();
        await store.SaveAsync(MakeResult(Tenant, "dup"), CancellationToken.None);

        var result = await store.GetBySessionIdsAsync(["dup", "dup"], Tenant, CancellationToken.None);

        result.Should().ContainSingle().Which.SessionId.Should().Be("dup");
    }
}
