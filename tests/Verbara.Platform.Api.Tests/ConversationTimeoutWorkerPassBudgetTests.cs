using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// conversations-close-after-inactivity, review round 1 (design D2, D4): the inactivity pass rides the timeout tick, and
/// that tick runs under the worker's per-attempt resilience budget (<c>BuildDefaultWorkerPolicy</c>: 10 s, 2 attempts).
/// The pass, its once-per-process clock reconcile included, MUST NOT be cut by that budget, and a tick attempt that fails
/// before the pass MUST NOT spend the pass's interval: the retry runs it.
/// </summary>
public sealed partial class ConversationTimeoutWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldCloseEveryTenantsIdleChats_WhenTheReconcileOutlastsTheTickBudget()
    {
        using var h = new InactivityHarness();
        h.Tenants.Add(OtherTenantId);
        var first = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        var second = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80), tenant: new TenantId(OtherTenantId));
        h.Distribution.ConversationTimeoutStartupDelayMs = 0;
        h.Distribution.ConversationTimeoutSweepIntervalMs = 50;
        // The default worker policy's shape, scaled down: each tenant's reconcile outlasts one attempt's budget.
        h.Policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(10))
            .WithTimeout(TimeSpan.FromMilliseconds(200))
            .Build();
        h.ReconcileDelay = TimeSpan.FromMilliseconds(600);
        var bothClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closes = 0;
        using var subscription = h.EventBus.Events.Subscribe(e =>
        {
            if (e is ConversationStateChangedEvent { NewState: nameof(ConversationState.Closed) }
                && Interlocked.Increment(ref closes) == 2)
            {
                bothClosed.TrySetResult();
            }
        });
        var worker = h.CreateWorker();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // The two closes are the causal signal; the bound only stops a red run from hanging.
            await bothClosed.Task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(ConversationState.Closed, (await h.GetAsync(first)).State);
        Assert.Equal(ConversationState.Closed, (await h.GetAsync(second)).State);
        Assert.Equal(1, h.PassCalls.Count(c => c == $"reconcile:{TestTenantId}"));
        Assert.Equal(1, h.PassCalls.Count(c => c == $"reconcile:{OtherTenantId}"));
    }

    [Fact]
    public async Task ProcessTimeoutsAsync_ShouldRunTheInactivityPassOnTheRetry_WhenTheAttemptFailedBeforeThePass()
    {
        using var h = new InactivityHarness();
        var conv = await h.SeedAsync(ConversationState.Active, idle: TimeSpan.FromHours(80));
        var faults = 1;
        h.Store.ListByStateAsync(Arg.Any<TenantId>(), ConversationState.Offered, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(c => faults-- > 0
                ? throw new TimeoutException("the attempt's budget ran out in the offer sweep")
                : h.Inner.ListByStateAsync(c.ArgAt<TenantId>(0), c.ArgAt<ConversationState>(1), c.ArgAt<int>(2), c.ArgAt<CancellationToken>(3)));
        var worker = h.CreateWorker();

        // Attempt 1 fails in the timeout sweep; the policy's retry runs the same tick again at the same clock.
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessTimeoutsAsync(CancellationToken.None));
        await worker.ProcessTimeoutsAsync(CancellationToken.None);

        Assert.Equal(ConversationState.Closed, (await h.GetAsync(conv)).State);
        Assert.Equal([$"reconcile:{TestTenantId}", $"list:{TestTenantId}"], h.PassCalls);
    }
}
