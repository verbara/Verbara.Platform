using Verbara.Platform.Core.Reports;
using Verbara.Platform.Storage.InMemory;
using NCrontab;

namespace Verbara.Platform.Api.Tests.Reports;

public class ReportSchedulerTests
{
    // ── NCrontab integration ─────────────────────────────────────────────────

    [Fact]
    public void Parse_ShouldReturnNextMonday8Am_WhenScheduleIsWeeklyMonday()
    {
        // "0 8 * * 1" = every Monday at 08:00
        var schedule = CrontabSchedule.Parse("0 8 * * 1");

        // Use a known Thursday as base so next occurrence is Monday
        var baseTime = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc); // Thursday
        var next = schedule.GetNextOccurrence(baseTime);

        next.DayOfWeek.Should().Be(DayOfWeek.Monday);
        next.Hour.Should().Be(8);
        next.Minute.Should().Be(0);
    }

    [Fact]
    public void Parse_ShouldAdvanceByOneWeek_WhenCalledAgainAfterFirstOccurrence()
    {
        var schedule = CrontabSchedule.Parse("0 8 * * 1");
        var baseTime = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc); // Thursday

        var first = schedule.GetNextOccurrence(baseTime);
        var second = schedule.GetNextOccurrence(first);

        (second - first).TotalDays.Should().BeApproximately(7.0, 0.1);
    }

    // ── InMemoryScheduledReportStore ─────────────────────────────────────────

    private static ScheduledReport BuildReport(string id, string tenantId, DateTimeOffset? nextRunAt, bool active = true) =>
        new()
        {
            ReportId = id,
            TenantId = tenantId,
            Name = $"Report {id}",
            ReportType = "interval_summary",
            Schedule = "0 8 * * 1",
            Recipients = "test@example.com",
            Format = "pdf",
            IsActive = active,
            CreatedBy = "system",
            CreatedAt = DateTimeOffset.UtcNow,
            NextRunAt = nextRunAt,
        };

    [Fact]
    public async Task GetDueReportsAsync_ShouldReturnOnlyDueReports_WhenMixOfDueAndFuture()
    {
        var store = new InMemoryScheduledReportStore();
        var now = DateTimeOffset.UtcNow;

        var dueReport = BuildReport("r1", "t1", now.AddMinutes(-5));
        var futureReport = BuildReport("r2", "t1", now.AddHours(1));
        var noNextRun = BuildReport("r3", "t1", null);

        await store.SaveAsync(dueReport, CancellationToken.None);
        await store.SaveAsync(futureReport, CancellationToken.None);
        await store.SaveAsync(noNextRun, CancellationToken.None);

        var due = await store.GetDueReportsAsync(now, CancellationToken.None);

        due.Should().HaveCount(1);
        due[0].ReportId.Should().Be("r1");
    }

    [Fact]
    public async Task GetDueReportsAsync_ShouldExcludeInactiveReports_EvenWhenOverdue()
    {
        var store = new InMemoryScheduledReportStore();
        var now = DateTimeOffset.UtcNow;

        var inactive = BuildReport("r1", "t1", now.AddMinutes(-5), active: false);
        await store.SaveAsync(inactive, CancellationToken.None);

        var due = await store.GetDueReportsAsync(now, CancellationToken.None);

        due.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistReport_WhenCalled()
    {
        var store = new InMemoryScheduledReportStore();
        var report = BuildReport("r1", "t1", null);

        await store.SaveAsync(report, CancellationToken.None);
        var fetched = await store.GetByIdAsync("r1", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.ReportId.Should().Be("r1");
        fetched.Name.Should().Be("Report r1");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveReport_WhenCalled()
    {
        var store = new InMemoryScheduledReportStore();
        var report = BuildReport("r1", "t1", null);

        await store.SaveAsync(report, CancellationToken.None);
        await store.DeleteAsync("r1", CancellationToken.None);
        var fetched = await store.GetByIdAsync("r1", CancellationToken.None);

        fetched.Should().BeNull();
    }

    [Fact]
    public async Task ListByTenantAsync_ShouldReturnOnlyTenantReports_WhenMultipleTenantsExist()
    {
        var store = new InMemoryScheduledReportStore();

        await store.SaveAsync(BuildReport("r1", "tenant-a", null), CancellationToken.None);
        await store.SaveAsync(BuildReport("r2", "tenant-a", null), CancellationToken.None);
        await store.SaveAsync(BuildReport("r3", "tenant-b", null), CancellationToken.None);

        var result = await store.ListByTenantAsync("tenant-a", CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(r => r.TenantId.Should().Be("tenant-a"));
    }

    [Fact]
    public async Task UpdateLastRunAsync_ShouldUpdateNextRunAt_WhenCalled()
    {
        var store = new InMemoryScheduledReportStore();
        var report = BuildReport("r1", "t1", null);
        await store.SaveAsync(report, CancellationToken.None);

        var lastRun = DateTimeOffset.UtcNow;
        var nextRun = lastRun.AddDays(7);
        await store.UpdateLastRunAsync("r1", lastRun, nextRun, CancellationToken.None);

        var updated = await store.GetByIdAsync("r1", CancellationToken.None);

        updated!.LastRunAt.Should().Be(lastRun);
        updated.NextRunAt.Should().Be(nextRun);
    }

    [Fact]
    public async Task SaveExecutionAsync_AndGetExecutionAsync_ShouldRoundTrip_WhenCalled()
    {
        var store = new InMemoryScheduledReportStore();
        var execution = new ReportExecution
        {
            ExecutionId = "exec-1",
            ReportId = "r1",
            TenantId = "t1",
            StartedAt = DateTimeOffset.UtcNow,
            Status = "completed",
            Format = "pdf",
        };

        await store.SaveExecutionAsync(execution, CancellationToken.None);
        var fetched = await store.GetExecutionAsync("exec-1", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.ExecutionId.Should().Be("exec-1");
        fetched.Status.Should().Be("completed");
    }
}
