using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Automation.Tests;

public sealed class TimerPollingServiceTests : IDisposable
{
    private readonly ITimerStore _timerStore = Substitute.For<ITimerStore>();
    private readonly IAutomationEngine _automationEngine = Substitute.For<IAutomationEngine>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId ConvId = EntityId.From("conv-001");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TimerPollingService _sut;

    public TimerPollingServiceTests()
    {
        _clock.UtcNow.Returns(Now);
        _sut = new TimerPollingService(
            _timerStore,
            _automationEngine,
            _clock,
            NullLogger<TimerPollingService>.Instance);
    }

    public void Dispose() => _sut.Dispose();

    private static ScheduledTimer MakeTimer(string timerId = "timer-001") =>
        new()
        {
            TimerId = EntityId.From(timerId),
            TenantId = Tenant,
            ConversationId = ConvId,
            CallbackRuleId = EntityId.From("rule-001"),
            FireAt = Now.AddSeconds(-30),
            IsFired = false,
            CreatedAt = Now.AddMinutes(-5),
        };

    [Fact]
    public async Task PollAsync_ShouldFireOverdueTimers_WhenTimersPresent()
    {
        var timer = MakeTimer();
        _timerStore.GetOverdueAsync(Now, 50, default).ReturnsForAnyArgs([timer]);
        _automationEngine.ProcessEventAsync(Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _timerStore.MarkFiredAsync(timer, default).Returns(Task.CompletedTask);

        await _sut.PollAsync(default);

        await _automationEngine.Received(1).ProcessEventAsync(
            Arg.Is<AutomationEvent>(e => e != null &&
                e.Trigger == AutomationTrigger.TimerElapsed &&
                e.ConversationId == ConvId &&
                e.TenantId == Tenant),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ShouldMarkFiredTimers_AfterProcessing()
    {
        var timer = MakeTimer();
        _timerStore.GetOverdueAsync(Now, 50, default).ReturnsForAnyArgs([timer]);
        _automationEngine.ProcessEventAsync(Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _timerStore.MarkFiredAsync(timer, default).Returns(Task.CompletedTask);

        await _sut.PollAsync(default);

        await _timerStore.Received(1).MarkFiredAsync(timer, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ShouldDoNothing_WhenNoOverdueTimers()
    {
        _timerStore.GetOverdueAsync(Now, 50, default).ReturnsForAnyArgs(new List<ScheduledTimer>());

        await _sut.PollAsync(default);

        await _automationEngine.DidNotReceive().ProcessEventAsync(Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
        await _timerStore.DidNotReceive().MarkFiredAsync(Arg.Any<ScheduledTimer>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ShouldMarkTimerFired_EvenWhenEngineThrows()
    {
        var timer = MakeTimer();
        _timerStore.GetOverdueAsync(Now, 50, default).ReturnsForAnyArgs([timer]);
        _automationEngine.ProcessEventAsync(Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Engine error")));
        _timerStore.MarkFiredAsync(timer, default).Returns(Task.CompletedTask);

        await _sut.PollAsync(default);

        await _timerStore.Received(1).MarkFiredAsync(timer, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ShouldProcessMultipleTimers()
    {
        var timer1 = MakeTimer("timer-001");
        var timer2 = MakeTimer("timer-002");

        _timerStore.GetOverdueAsync(Now, 50, default).ReturnsForAnyArgs([timer1, timer2]);
        _automationEngine.ProcessEventAsync(Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _timerStore.MarkFiredAsync(Arg.Any<ScheduledTimer>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.PollAsync(default);

        await _automationEngine.Received(2).ProcessEventAsync(Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
        await _timerStore.Received(2).MarkFiredAsync(Arg.Any<ScheduledTimer>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ShouldPassCurrentTimeAsOccurredAt()
    {
        var timer = MakeTimer();
        _timerStore.GetOverdueAsync(Now, 50, default).ReturnsForAnyArgs([timer]);
        _automationEngine.ProcessEventAsync(Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _timerStore.MarkFiredAsync(timer, default).Returns(Task.CompletedTask);

        await _sut.PollAsync(default);

        await _automationEngine.Received(1).ProcessEventAsync(
            Arg.Is<AutomationEvent>(e => e != null && e.OccurredAt == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ShouldRespectLimit_WhenStoreLimits()
    {
        // Simulate store returning only 50 timers (limit enforced by store)
        var timers = Enumerable.Range(1, 50)
            .Select(i => MakeTimer($"timer-{i:D3}"))
            .ToList();

        _timerStore.GetOverdueAsync(Now, 50, default).ReturnsForAnyArgs(timers);
        _automationEngine.ProcessEventAsync(Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _timerStore.MarkFiredAsync(Arg.Any<ScheduledTimer>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.PollAsync(default);

        await _automationEngine.Received(50).ProcessEventAsync(Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ShouldPassCorrectLimitToStore()
    {
        _timerStore.GetOverdueAsync(Now, 50, default).ReturnsForAnyArgs(new List<ScheduledTimer>());

        await _sut.PollAsync(default);

        await _timerStore.Received(1).GetOverdueAsync(
            Arg.Any<DateTimeOffset>(),
            50,
            Arg.Any<CancellationToken>());
    }
}
