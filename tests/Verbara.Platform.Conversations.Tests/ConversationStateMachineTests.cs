namespace Verbara.Platform.Conversations.Tests;

public class ConversationStateMachineTests
{
    [Theory]
    [InlineData(ConversationState.Queued, ConversationState.Offered)]
    [InlineData(ConversationState.Queued, ConversationState.Abandoned)]
    [InlineData(ConversationState.Offered, ConversationState.Active)]
    [InlineData(ConversationState.Offered, ConversationState.Queued)]
    [InlineData(ConversationState.Active, ConversationState.OnHold)]
    [InlineData(ConversationState.Active, ConversationState.Consulting)]
    [InlineData(ConversationState.Active, ConversationState.WrapUp)]
    [InlineData(ConversationState.Active, ConversationState.Escalated)]
    [InlineData(ConversationState.OnHold, ConversationState.Active)]
    [InlineData(ConversationState.Consulting, ConversationState.Active)]
    [InlineData(ConversationState.WrapUp, ConversationState.Resolved)]
    [InlineData(ConversationState.WrapUp, ConversationState.Closed)]
    [InlineData(ConversationState.WaitingForCustomer, ConversationState.Active)]
    [InlineData(ConversationState.WaitingForCustomer, ConversationState.Snoozed)]
    [InlineData(ConversationState.Snoozed, ConversationState.Active)]
    [InlineData(ConversationState.Resolved, ConversationState.Active)]
    [InlineData(ConversationState.Resolved, ConversationState.Closed)]
    [InlineData(ConversationState.Escalated, ConversationState.Active)]
    public void CanTransition_ShouldReturnTrue_WhenValidTransition(
        ConversationState from, ConversationState to)
    {
        ConversationStateMachine.CanTransition(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(ConversationState.Closed, ConversationState.Active)]
    [InlineData(ConversationState.Abandoned, ConversationState.Active)]
    [InlineData(ConversationState.Merged, ConversationState.Active)]
    [InlineData(ConversationState.Spam, ConversationState.Active)]
    [InlineData(ConversationState.Queued, ConversationState.WrapUp)]
    [InlineData(ConversationState.Active, ConversationState.Queued)]
    public void CanTransition_ShouldReturnFalse_WhenInvalidTransition(
        ConversationState from, ConversationState to)
    {
        ConversationStateMachine.CanTransition(from, to).Should().BeFalse();
    }

    [Fact]
    public void IsTerminal_ShouldReturnTrue_WhenClosedOrAbandoned()
    {
        ConversationStateMachine.IsTerminal(ConversationState.Closed).Should().BeTrue();
        ConversationStateMachine.IsTerminal(ConversationState.Abandoned).Should().BeTrue();
        ConversationStateMachine.IsTerminal(ConversationState.Merged).Should().BeTrue();
        ConversationStateMachine.IsTerminal(ConversationState.Spam).Should().BeTrue();
    }

    [Fact]
    public void IsTerminal_ShouldReturnFalse_WhenActive()
    {
        ConversationStateMachine.IsTerminal(ConversationState.Active).Should().BeFalse();
        ConversationStateMachine.IsTerminal(ConversationState.Resolved).Should().BeFalse();
    }

    [Fact]
    public void EnsureTransition_ShouldThrow_WhenInvalidTransition()
    {
        var act = () => ConversationStateMachine.EnsureTransition(
            ConversationState.Closed, ConversationState.Active);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid conversation state transition*");
    }

    [Fact]
    public void EnsureTransition_ShouldNotThrow_WhenValidTransition()
    {
        var act = () => ConversationStateMachine.EnsureTransition(
            ConversationState.Queued, ConversationState.Offered);

        act.Should().NotThrow();
    }
}
