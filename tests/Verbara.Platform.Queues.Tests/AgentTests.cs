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
        agent.Capacity.MaxVoice.Should().Be(1);
        agent.Capacity.MaxChat.Should().Be(3);
    }

    [Fact]
    public void HasCapacity_ShouldReturnTrue_WhenBelowLimit()
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

        agent.HasCapacity(ChannelType.Voice).Should().BeTrue();
    }

    [Fact]
    public void HasCapacity_ShouldReturnFalse_WhenNotAvailable()
    {
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
