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
    // D1 (close-chat-without-typification-licence): /close walks Active → WrapUp → Closed; there is no
    // direct edge, so the supervisor force-close, webhook and automation closes stay as narrow as they are.
    [InlineData(ConversationState.Active, ConversationState.Closed)]
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

    // conversations-close-after-inactivity task 1.3 (design D3): the inactivity close has its own, explicitly named
    // check; the shared transition table gets no new edge, so the generic closes keep refusing an agent-held row.

    public static TheoryData<ConversationState> AllStates()
    {
        var data = new TheoryData<ConversationState>();
        foreach (var state in Enum.GetValues<ConversationState>())
            data.Add(state);
        return data;
    }

    // Every ConversationState value is enumerated, so a state added later fails here until it is classified.
    [Theory]
    [MemberData(nameof(AllStates))]
    public void CanCloseForInactivity_ShouldBeTrueForExactlyTheFiveInScopeStates_WhenAskedForEveryState(ConversationState state)
    {
        var expected = state switch
        {
            ConversationState.Active or ConversationState.OnHold or ConversationState.Consulting
                or ConversationState.WaitingForCustomer or ConversationState.Resolved => true,
            ConversationState.Queued or ConversationState.Offered or ConversationState.WrapUp
                or ConversationState.Snoozed or ConversationState.Escalated or ConversationState.Closed
                or ConversationState.Abandoned or ConversationState.Merged or ConversationState.Spam => false,
            _ => throw new InvalidOperationException(
                $"ConversationState.{state} is not classified for the inactivity close (design D3); classify it here."),
        };

        ConversationStateMachine.CanCloseForInactivity(state).Should().Be(expected);
    }

    [Theory]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.OnHold)]
    [InlineData(ConversationState.Consulting)]
    public void EnsureTransition_ShouldStillRefuseClosed_WhenTheRowIsAgentHeld(ConversationState from)
    {
        ConversationStateMachine.CanTransition(from, ConversationState.Closed).Should().BeFalse(
            "the table gets no new edge: the generic closes keep refusing an agent-held conversation (D3)");
        var act = () => ConversationStateMachine.EnsureTransition(from, ConversationState.Closed);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(ConversationState.WaitingForCustomer, true)]
    [InlineData(ConversationState.Resolved, true)]
    [InlineData(ConversationState.Snoozed, false)]
    [InlineData(ConversationState.Escalated, false)]
    public void CanTransition_ShouldKeepTheExistingCloseEdges_WhenTheInactivityCheckIsAdded(ConversationState from, bool allowed)
    {
        ConversationStateMachine.CanTransition(from, ConversationState.Closed).Should().Be(allowed);
    }
}
