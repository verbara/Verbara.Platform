using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// conversations-close-after-inactivity task 2.8 (design D2, "Clock reconciliation on every start, before the first
/// sweep"; spec <c>conversation-inactivity-close</c>, "A message stored by an older binary still keeps the conversation
/// open after an upgrade"): the pass half. The inactivity pass reconciles each tenant's clocks once per process, before
/// that tenant's first <c>ListInactiveAsync</c>; a reconcile that throws skips that tenant's sweep for the pass and is
/// retried on the next one; with the switch off nothing reconciles; one Information line per tenant records the
/// reconcile's duration and the rows it advanced.
/// </summary>
public sealed partial class ConversationTimeoutWorkerTests
{
    private static readonly EventId ReconcileLogEvent = new(7606);
    private static readonly EventId ReconcileFailedEvent = new(7607);

    /// <summary>Stores an inbound message as an older binary did: the conversation row (its clock) is not touched.</summary>
    private static Task StoreOldBinaryInboundAsync(InactivityHarness h, Conversation conv, DateTimeOffset createdAt) =>
        h.Messages.SaveAsync(new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conv.ConversationId,
            TenantId = conv.TenantId,
            Direction = MessageDirection.Inbound,
            Channel = conv.Channel,
            Content = new MessageEnvelope([new TextBlock("hola")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = createdAt,
        }, CancellationToken.None);

    [Fact]
    public async Task InactivityPass_ShouldKeepTheChatOpen_WhenAnOlderBinaryStoredAMessageNewerThanTheClock()
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        await StoreOldBinaryInboundAsync(h, conv, h.Now.AddHours(-1));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        var stored = await h.GetAsync(conv);
        Assert.Equal(ConversationState.Active, stored.State);
        Assert.Equal(h.Now.AddHours(-1), stored.LastMessageAt);
        Assert.Empty(h.ClosedEvents);
    }

    [Fact]
    public async Task InactivityPass_ShouldReconcileEachTenantOnceBeforeItsFirstList_WhenThePassRunsTwice()
    {
        using var h = new InactivityHarness();
        h.Tenants.Add(OtherTenantId);
        var worker = h.CreateWorker();

        await worker.ProcessTimeoutsAsync(CancellationToken.None);
        h.Now = h.Now.AddSeconds(h.Options.InactivityCloseSweepIntervalSeconds + 1);
        await worker.ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(
            [
                $"reconcile:{TestTenantId}", $"list:{TestTenantId}",
                $"reconcile:{OtherTenantId}", $"list:{OtherTenantId}",
                $"list:{TestTenantId}",
                $"list:{OtherTenantId}",
            ],
            h.PassCalls);
    }

    [Fact]
    public async Task InactivityPass_ShouldSkipOnlyThatTenantAndRetryNextPass_WhenItsReconcileThrows()
    {
        using var h = new InactivityHarness();
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        h.Tenants.Add(OtherTenantId);
        var failing = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        var other = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80), tenant: new TenantId(OtherTenantId));
        var faults = 1;
        h.BeforeReconcile = tenant => tenant.Value == TestTenantId && faults-- > 0
            ? throw new InvalidOperationException("reconcile failed")
            : Task.CompletedTask;
        var worker = h.CreateWorker();

        await worker.ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Active, (await h.GetAsync(failing)).State);
        Assert.Equal(ConversationState.Closed, (await h.GetAsync(other)).State);
        Assert.DoesNotContain($"list:{TestTenantId}", h.PassCalls);
        var warning = Assert.Single(logger.Entries, e => e.EventId.Id == ReconcileFailedEvent.Id);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<InvalidOperationException>(warning.Exception);
        Assert.Contains($"tenant {TestTenantId}", warning.Message, StringComparison.Ordinal);

        h.Now = h.Now.AddSeconds(h.Options.InactivityCloseSweepIntervalSeconds + 1);
        await worker.ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Closed, (await h.GetAsync(failing)).State);
        Assert.Equal(2, h.PassCalls.Count(c => c == $"reconcile:{TestTenantId}"));
        Assert.Equal(1, h.PassCalls.Count(c => c == $"reconcile:{OtherTenantId}"));
    }

    [Fact]
    public async Task InactivityPass_ShouldNeitherReconcileNorClose_WhenTheSwitchIsOffUntilAStartWithItOn()
    {
        using var h = new InactivityHarness();
        h.Options.InactivityCloseEnabled = false;
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        await StoreOldBinaryInboundAsync(h, conv, h.Now.AddHours(-1));

        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Empty(h.PassCalls);
        Assert.Equal(ConversationState.Active, (await h.GetAsync(conv)).State);
        Assert.Equal(h.Now.AddHours(-80), (await h.GetAsync(conv)).LastMessageAt);

        // The switch is turned on with a restart: the new process reconciles before its first sweep.
        h.Options.InactivityCloseEnabled = true;
        await h.CreateWorker().ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal([$"reconcile:{TestTenantId}", $"list:{TestTenantId}"], h.PassCalls);
        var stored = await h.GetAsync(conv);
        Assert.Equal(ConversationState.Active, stored.State);
        Assert.Equal(h.Now.AddHours(-1), stored.LastMessageAt);
    }

    [Fact]
    public async Task InactivityPass_ShouldLogTheReconcileDurationAndRowsAdvancedOncePerTenant_WhenItReconciles()
    {
        using var h = new InactivityHarness();
        var logger = new CapturingLogger<ConversationTimeoutWorker>();
        h.Logger = logger;
        var a = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        var b = await h.SeedAsync(ConversationState.OnHold, idle: TimeSpan.FromHours(90));
        await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(10)); // no newer message: not advanced
        await StoreOldBinaryInboundAsync(h, a, h.Now.AddHours(-1));
        await StoreOldBinaryInboundAsync(h, b, h.Now.AddHours(-2));
        var worker = h.CreateWorker();

        await worker.ProcessTimeoutsAsync(CancellationToken.None);
        h.Now = h.Now.AddSeconds(h.Options.InactivityCloseSweepIntervalSeconds + 1);
        await worker.ProcessTimeoutsAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == ReconcileLogEvent.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains($"tenant {TestTenantId}", entry.Message, StringComparison.Ordinal);
        Assert.Contains("advanced 2 rows", entry.Message, StringComparison.Ordinal);
        Assert.Matches(@"in \d+ ms", entry.Message);
    }
}
