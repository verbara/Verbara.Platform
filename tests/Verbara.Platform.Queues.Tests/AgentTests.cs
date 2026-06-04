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
