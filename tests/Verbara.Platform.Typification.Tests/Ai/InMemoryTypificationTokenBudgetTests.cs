using Verbara.Platform.Core;
using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Typification.Tests.Ai;

/// <summary>
/// E3 — unit tests for <see cref="InMemoryTypificationTokenBudget"/>, the per-(tenant, UTC-day)
/// running token-sum accumulator that backs the fail-closed daily LLM token budget.
/// </summary>
public sealed class InMemoryTypificationTokenBudgetTests
{
    private static readonly TenantId Tenant = new("tenant-budget");

    /// <summary>Mutable test clock so a test can advance the UTC day to exercise rollover.</summary>
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
        public FakeClock(DateTimeOffset now) => UtcNow = now;
    }

    [Fact]
    public async Task IsOverBudget_ShouldReturnFalse_WhenNoUsageRecorded()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new InMemoryTypificationTokenBudget(clock);

        var over = await sut.IsOverBudgetAsync(Tenant, dailyBudget: 1000, CancellationToken.None);

        over.Should().BeFalse();
    }

    [Fact]
    public async Task IsOverBudget_ShouldReturnFalse_WhenDailySumBelowBudget()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new InMemoryTypificationTokenBudget(clock);

        await sut.RecordUsageAsync(Tenant, 999, CancellationToken.None);

        var over = await sut.IsOverBudgetAsync(Tenant, dailyBudget: 1000, CancellationToken.None);

        over.Should().BeFalse("999 < 1000");
    }

    [Fact]
    public async Task IsOverBudget_ShouldReturnTrue_WhenDailySumReachesBudget()
    {
        // Boundary case: reaching the budget exactly (>=) exhausts it.
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new InMemoryTypificationTokenBudget(clock);

        await sut.RecordUsageAsync(Tenant, 600, CancellationToken.None);
        await sut.RecordUsageAsync(Tenant, 400, CancellationToken.None);

        var over = await sut.IsOverBudgetAsync(Tenant, dailyBudget: 1000, CancellationToken.None);

        over.Should().BeTrue("600 + 400 == 1000 (>= boundary inclusive)");
    }

    [Fact]
    public async Task IsOverBudget_ShouldReturnTrue_WhenDailySumExceedsBudget()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new InMemoryTypificationTokenBudget(clock);

        await sut.RecordUsageAsync(Tenant, 1500, CancellationToken.None);

        var over = await sut.IsOverBudgetAsync(Tenant, dailyBudget: 1000, CancellationToken.None);

        over.Should().BeTrue();
    }

    [Fact]
    public async Task RecordUsage_ShouldIgnore_WhenTokensNonPositive()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new InMemoryTypificationTokenBudget(clock);

        await sut.RecordUsageAsync(Tenant, 0, CancellationToken.None);
        await sut.RecordUsageAsync(Tenant, -50, CancellationToken.None);

        var over = await sut.IsOverBudgetAsync(Tenant, dailyBudget: 1, CancellationToken.None);

        over.Should().BeFalse("non-positive usage is ignored, so the sum stays 0");
    }

    [Fact]
    public async Task RecordUsage_ShouldRollOver_WhenUtcDayChanges()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 16, 23, 0, 0, TimeSpan.Zero));
        var sut = new InMemoryTypificationTokenBudget(clock);

        // Day 1: exhaust the budget.
        await sut.RecordUsageAsync(Tenant, 1000, CancellationToken.None);
        (await sut.IsOverBudgetAsync(Tenant, dailyBudget: 1000, CancellationToken.None))
            .Should().BeTrue("day-1 sum reached the budget");

        // Advance the clock past UTC midnight into the next day.
        clock.UtcNow = new DateTimeOffset(2026, 6, 17, 1, 0, 0, TimeSpan.Zero);

        // The new day starts fresh — yesterday's sum does not carry over.
        (await sut.IsOverBudgetAsync(Tenant, dailyBudget: 1000, CancellationToken.None))
            .Should().BeFalse("the UTC-day rolled over so the running sum reset");

        // Recording on the new day accumulates independently from day 1.
        await sut.RecordUsageAsync(Tenant, 1000, CancellationToken.None);
        (await sut.IsOverBudgetAsync(Tenant, dailyBudget: 1000, CancellationToken.None))
            .Should().BeTrue("day-2 sum now reaches the budget");
    }

    [Fact]
    public async Task RecordUsage_ShouldNotLoseUpdates_UnderConcurrentRecords()
    {
        // Lost-update guard: fire many parallel RecordUsageAsync calls against one accumulator and
        // assert the running sum is EXACTLY their total (no read-modify-write races dropped any).
        // This locks in the atomicity of the AddOrUpdate accumulator against a future refactor to a
        // non-atomic read-modify-write.
        const int Records = 100;
        const long TokensPerRecord = 10;
        const long ExpectedSum = Records * TokensPerRecord; // 1000

        var clock = new FakeClock(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new InMemoryTypificationTokenBudget(clock);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, Records),
            async (_, ct) => await sut.RecordUsageAsync(Tenant, TokensPerRecord, ct));

        // The accumulated sum must be exactly 1000 — over a budget of (1000 - 1) yet NOT over
        // (1000 + 1). Any lost update would leave the sum < 1000 and fail the first assertion.
        (await sut.IsOverBudgetAsync(Tenant, dailyBudget: ExpectedSum - 1, CancellationToken.None))
            .Should().BeTrue("the accumulated sum must be >= 999 (no concurrent update was lost)");
        (await sut.IsOverBudgetAsync(Tenant, dailyBudget: ExpectedSum + 1, CancellationToken.None))
            .Should().BeFalse("the accumulated sum must be < 1001 (no update was double-counted)");
    }

    [Fact]
    public async Task IsOverBudget_ShouldBeIsolatedPerTenant_WhenDifferentTenantsRecordUsage()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new InMemoryTypificationTokenBudget(clock);

        var other = new TenantId("tenant-other");
        await sut.RecordUsageAsync(Tenant, 5000, CancellationToken.None);

        (await sut.IsOverBudgetAsync(other, dailyBudget: 1000, CancellationToken.None))
            .Should().BeFalse("usage for one tenant must not count against another");
        (await sut.IsOverBudgetAsync(Tenant, dailyBudget: 1000, CancellationToken.None))
            .Should().BeTrue();
    }
}
