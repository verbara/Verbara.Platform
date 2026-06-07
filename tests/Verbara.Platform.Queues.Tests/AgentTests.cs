using Verbara.Platform.Core;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Queues.Tests;

public class AgentTests
{
    [Fact]
    public void Constructor_ShouldCreateAgent_WhenValidInput()
    {
        var agent = new Agent
        {
            AgentId = EntityId.From("a-001"),
            TenantId = new TenantId("t1"),
            UserId = EntityId.From("u-001"),
            DisplayName = "Agent Smith",
            State = AgentState.Offline,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        agent.State.Should().Be(AgentState.Offline);
        // W6 — a freshly constructed agent carries an all-null override
        // ("inherit every tenant default"); the effective per-channel maxima are
        // resolved later from the tenant default + this override.
        agent.CapacityOverride.MaxVoice.Should().BeNull();
        agent.CapacityOverride.MaxChat.Should().BeNull();
    }

    [Fact]
    public void HasCapacity_ShouldReturnTrue_WhenRoutable()
    {
        // W6 — HasCapacity is now a pure routability check (the per-channel /
        // MaxTotal gate moved to IAgentCapacityService + the resolver). A routable
        // state must answer true regardless of the channel argument.
        var agent = new Agent
        {
            AgentId = EntityId.From("a-001"),
            TenantId = new TenantId("t1"),
            UserId = EntityId.From("u-001"),
            DisplayName = "Agent",
            State = AgentState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        agent.HasCapacity(ChannelType.Voice).Should().BeTrue();
    }

    [Fact]
    public void HasCapacity_ShouldReturnFalse_WhenNotRoutable()
    {
        // W6 — a non-routable state (Break) is never eligible for new work.
        var agent = new Agent
        {
            AgentId = EntityId.From("a-001"),
            TenantId = new TenantId("t1"),
            UserId = EntityId.From("u-001"),
            DisplayName = "Agent",
            State = AgentState.Break,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        agent.HasCapacity(ChannelType.Voice).Should().BeFalse();
    }

    [Fact]
    public void CanAcceptWork_ShouldReturnTrue_WhenAvailableOrBusy()
    {
        var agent = new Agent
        {
            AgentId = EntityId.From("a-001"),
            TenantId = new TenantId("t1"),
            UserId = EntityId.From("u-001"),
            DisplayName = "Agent",
            State = AgentState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        agent.CanAcceptWork.Should().BeTrue();
    }

    [Theory]
    [InlineData(AgentState.Busy)]
    [InlineData(AgentState.DND)]
    [InlineData(AgentState.ACW)]
    [InlineData(AgentState.Break)]
    public void ForceOffline_ShouldSetOfflineFromAnyState_WhenInvoked(
        AgentState initial)
    {
        var agent = NewAgent(initial);

        agent.ForceOffline();

        agent.State.Should().Be(AgentState.Offline);
    }

    [Fact]
    public void ForceOffline_ShouldSetOfflineSince_WhenEnteringOffline()
    {
        var now = new DateTimeOffset(2026, 6, 6, 10, 0, 0, TimeSpan.Zero);
        var agent = NewAgent(AgentState.Busy);

        agent.ForceOffline(now);

        agent.State.Should().Be(AgentState.Offline);
        agent.OfflineSince.Should().Be(now);
    }

    [Fact]
    public void ForceOffline_ShouldNotResetOfflineSince_WhenCalledAgain()
    {
        var t1 = new DateTimeOffset(2026, 6, 6, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 6, 6, 10, 5, 0, TimeSpan.Zero);
        var agent = NewAgent(AgentState.Busy);

        agent.ForceOffline(t1);
        agent.ForceOffline(t2);

        agent.OfflineSince.Should().Be(t1);
    }

    [Fact]
    public void TransitionTo_ShouldSetOfflineSince_WhenTargetOffline()
    {
        var now = new DateTimeOffset(2026, 6, 6, 10, 0, 0, TimeSpan.Zero);
        var agent = NewAgent(AgentState.Available);

        agent.TransitionTo(AgentState.Offline, now);

        agent.State.Should().Be(AgentState.Offline);
        agent.OfflineSince.Should().Be(now);
    }

    [Fact]
    public void TransitionTo_ShouldClearOfflineSince_WhenLeavingOffline()
    {
        var now = new DateTimeOffset(2026, 6, 6, 10, 0, 0, TimeSpan.Zero);
        var agent = NewAgent(AgentState.Offline);
        agent.OfflineSince = now;

        agent.TransitionTo(AgentState.Available);

        agent.State.Should().Be(AgentState.Available);
        agent.OfflineSince.Should().BeNull();
    }

    [Fact]
    public void OfflineSince_ShouldStartFresh_WhenSecondOfflineEpisodeAfterReturn()
    {
        // The exact stale-stamp scenario: Offline(t1) → Available (cleared) → Offline(t2)
        // must yield OfflineSince == t2, not a stale t1 (which would make the grace appear
        // already elapsed and re-queue the second episode's work immediately).
        var t1 = new DateTimeOffset(2026, 6, 6, 10, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(5);
        var agent = NewAgent(AgentState.Available);

        agent.ForceOffline(t1);
        agent.OfflineSince.Should().Be(t1);

        agent.TransitionTo(AgentState.Available);
        agent.OfflineSince.Should().BeNull();

        agent.ForceOffline(t2);
        agent.OfflineSince.Should().Be(t2);
    }

    [Fact]
    public void ApplyPendingState_ShouldSetStateAndClearPending_WhenPendingSet()
    {
        var agent = NewAgent(AgentState.Available);
        agent.PendingState = AgentState.Break;
        agent.PendingReason = "coffee";
        agent.PendingSince = DateTimeOffset.UtcNow;

        agent.ApplyPendingState();

        agent.State.Should().Be(AgentState.Break);
        agent.PendingState.Should().BeNull();
        agent.PendingReason.Should().BeNull();
        agent.PendingSince.Should().BeNull();
        agent.HasPendingPause.Should().BeFalse();
    }

    [Fact]
    public void ApplyPendingState_ShouldBeNoOp_WhenNoPending()
    {
        var agent = NewAgent(AgentState.Available);

        agent.ApplyPendingState();

        agent.State.Should().Be(AgentState.Available);
        agent.PendingState.Should().BeNull();
    }

    [Fact]
    public void ApplyPendingState_ShouldApplyFromAcw_WhenTargetTransitionWouldBeInvalid()
    {
        // ACW -> Lunch is NOT a valid TransitionTo edge; ApplyPendingState bypasses
        // EnsureTransition on purpose because the target was validated at request time.
        var agent = NewAgent(AgentState.ACW);
        agent.PendingState = AgentState.Lunch;
        agent.PendingReason = "lunch";
        agent.PendingSince = DateTimeOffset.UtcNow;

        agent.ApplyPendingState();

        agent.State.Should().Be(AgentState.Lunch);
        agent.HasPendingPause.Should().BeFalse();
        agent.PendingState.Should().BeNull();
        agent.PendingReason.Should().BeNull();
        agent.PendingSince.Should().BeNull();
    }

    [Fact]
    public void HasPendingPause_ShouldBeTrue_WhenPendingStateSet()
    {
        var agent = NewAgent(AgentState.Busy);

        agent.HasPendingPause.Should().BeFalse();

        agent.PendingState = AgentState.Break;

        agent.HasPendingPause.Should().BeTrue();
    }

    private static Agent NewAgent(AgentState state) => new()
    {
        AgentId = EntityId.From("a-001"),
        TenantId = new TenantId("t1"),
        UserId = EntityId.From("u-001"),
        DisplayName = "Agent",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
