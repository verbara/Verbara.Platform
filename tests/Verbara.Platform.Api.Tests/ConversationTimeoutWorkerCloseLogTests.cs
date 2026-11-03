using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// conversations-close-after-inactivity task 2.7 (design D12, D13; spec <c>conversation-inactivity-close</c>, "A close
/// that catches up on a backlog MUST be marked inactivity-backlog" and "Every system close MUST be counted by reason and
/// every inactivity close MUST be logged"): the backlog mark the conditional close records, the close counter the pass
/// increments after a won close, and the one Information close-log line with the tenant, channel, reason and idle-age
/// bucket.
/// </summary>
public sealed partial class ConversationTimeoutWorkerTests
{
    private static readonly EventId InactivityCloseLogEvent = new(7604);

    /// <summary>The close-log lines (EventId 7604) a capturing logger received.</summary>
    internal static IReadOnlyList<string> CloseLogsOf(CapturingLogger<ConversationTimeoutWorker> logger) =>
        [.. logger.Entries.Where(e => e.EventId.Id == InactivityCloseLogEvent.Id).Select(e => e.Message)];

    [Theory]
    // 72 h window, 60 s interval: grace = max(1 h, 2 x 60 s) = 1 h, so the backlog starts past 73 h of idle time.
    [InlineData(60, 200 * 60, ConversationCloseReasons.InactivityBacklog)]
    [InlineData(60, 72 * 60 + 1, ConversationCloseReasons.Inactivity)]
    [InlineData(60, 73 * 60 + 30, ConversationCloseReasons.InactivityBacklog)]
    // 3600 s interval: grace = 2 h, so 73 h 30 min is still a steady-state close.
    [InlineData(3600, 73 * 60 + 30, ConversationCloseReasons.Inactivity)]
    public async Task InactivityPass_ShouldRecordTheSameReasonInMetadataAuditCounterAndLog_WhenItClosesAChat(
        int sweepIntervalSeconds, int idleMinutes, string expectedReason)
    {
        using var h = new InactivityHarness();
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        h.Options.InactivityCloseSweepIntervalSeconds = sweepIntervalSeconds;
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromMinutes(idleMinutes));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        var stored = await h.GetAsync(conv);
        Assert.Equal(ConversationState.Closed, stored.State);
        Assert.Equal(expectedReason, stored.Metadata[ConversationCloseReasons.MetadataKey]);
        await h.Audit.Received(1).RecordAsync(
            h.Tenant, "conversations", "conversation.closed", "info", SystemActor, "system",
            conv.ConversationId.Value, "Conversation", Arg.Any<Guid?>(), Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(m => m != null && m["reason"] == expectedReason),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        var counted = Assert.Single(h.CountedByReason);
        Assert.Equal(expectedReason, counted.Key);
        Assert.Equal(1, counted.Value);
        var line = Assert.Single(CloseLogsOf(logger));
        Assert.Contains($"reason {expectedReason},", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InactivityPass_ShouldCountAndLogOnce_WhenTwoWorkersCloseOneHundredHourChat()
    {
        var result = await RunTwoWorkersAsync(unconditionalClose: false, idle: TimeSpan.FromHours(100));

        Assert.Equal(ConversationState.Closed, result.FinalState);
        Assert.Equal(2, result.CloseAttempts); // both workers reached the close: the window was held open
        var counted = Assert.Single(result.Counted);
        Assert.Equal(ConversationCloseReasons.InactivityBacklog, counted.Key);
        Assert.Equal(1, counted.Value);
        var line = Assert.Single(result.CloseLogs);
        Assert.Contains($"tenant {TestTenantId}", line, StringComparison.Ordinal);
        Assert.Contains("channel WebChat", line, StringComparison.Ordinal);
        Assert.Contains($"reason {ConversationCloseReasons.InactivityBacklog},", line, StringComparison.Ordinal);
        Assert.Contains("idle 96-168h", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-0030 M-load control for the test above: the same held window with the unconditional close. The counter and
    /// the log must double, proving the hold puts both workers inside the window, so the candidate's single count means
    /// the conditional close decided it.
    /// </summary>
    [Fact]
    public async Task InactivityPass_ShouldCountAndLogTwice_WhenTheCloseIsAnUnconditionalSave_Control()
    {
        var result = await RunTwoWorkersAsync(unconditionalClose: true, idle: TimeSpan.FromHours(100));

        Assert.Equal(2, result.CloseAttempts);
        var counted = Assert.Single(result.Counted);
        Assert.Equal(ConversationCloseReasons.InactivityBacklog, counted.Key);
        Assert.Equal(2, counted.Value);
        Assert.Equal(2, result.CloseLogs.Count);
    }

    [Theory]
    [InlineData(24, 30, "<72h")]
    [InlineData(72, 80, "72-96h")]
    [InlineData(72, 200, ">168h")]
    public async Task InactivityPass_ShouldLogTheIdleAgeBucket_WhenItClosesAChat(int windowHours, int idleHours, string bucket)
    {
        using var h = new InactivityHarness();
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        h.Options.InactivityCloseAfterHours = windowHours;
        await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(idleHours));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == InactivityCloseLogEvent.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains($"idle {bucket}", entry.Message, StringComparison.Ordinal);
        Assert.Contains($"tenant {TestTenantId}", entry.Message, StringComparison.Ordinal);
        Assert.Contains("channel WebChat", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InactivityPass_ShouldNotCountOrLog_WhenTheCloseIsLost()
    {
        using var h = new InactivityHarness();
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        h.AfterList = () => h.Inner.TryClaimForInboundAsync(h.Tenant, conv.ConversationId, h.Now, CancellationToken.None);

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(1, h.CloseAttempts);
        Assert.Empty(h.CountedByReason);
        Assert.Empty(CloseLogsOf(logger));
    }
}
