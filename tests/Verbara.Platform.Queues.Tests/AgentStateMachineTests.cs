using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Queues.Tests;

public class AgentStateMachineTests
{
    [Theory]
    [InlineData(AgentState.Offline, AgentState.Available)]
    [InlineData(AgentState.Available, AgentState.Busy)]
    [InlineData(AgentState.Available, AgentState.Break)]
    [InlineData(AgentState.Available, AgentState.Lunch)]
    [InlineData(AgentState.Available, AgentState.Offline)]
    [InlineData(AgentState.Busy, AgentState.Available)]
    [InlineData(AgentState.Busy, AgentState.ACW)]
    [InlineData(AgentState.ACW, AgentState.Available)]
    [InlineData(AgentState.ACW, AgentState.Break)]
    [InlineData(AgentState.Break, AgentState.Available)]
    [InlineData(AgentState.Lunch, AgentState.Available)]
    [InlineData(AgentState.Training, AgentState.Available)]
    [InlineData(AgentState.DND, AgentState.Available)]
    public void CanTransition_ShouldReturnTrue_WhenValidTransition(
        AgentState from, AgentState to)
    {
        AgentStateMachine.CanTransition(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(AgentState.Offline, AgentState.Busy)]
    [InlineData(AgentState.Break, AgentState.Busy)]
    [InlineData(AgentState.ACW, AgentState.Busy)]
    public void CanTransition_ShouldReturnFalse_WhenInvalidTransition(
        AgentState from, AgentState to)
    {
        AgentStateMachine.CanTransition(from, to).Should().BeFalse();
    }

    [Fact]
    public void CanTransition_ShouldReturnTrue_WhenBusyToOffline()
    {
        AgentStateMachine.CanTransition(AgentState.Busy, AgentState.Offline)
            .Should().BeTrue();
    }

    [Fact]
    public void IsRoutable_ShouldReturnTrue_WhenAvailableOrBusy()
    {
        AgentStateMachine.IsRoutable(AgentState.Available).Should().BeTrue();
        AgentStateMachine.IsRoutable(AgentState.Busy).Should().BeTrue();
    }

    [Fact]
    public void IsRoutable_ShouldReturnFalse_WhenOffline()
    {
        AgentStateMachine.IsRoutable(AgentState.Offline).Should().BeFalse();
        AgentStateMachine.IsRoutable(AgentState.Break).Should().BeFalse();
    }
}
