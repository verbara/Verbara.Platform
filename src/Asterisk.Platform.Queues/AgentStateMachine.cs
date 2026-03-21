namespace Asterisk.Platform.Queues;

public static class AgentStateMachine
{
    private static readonly HashSet<(AgentState From, AgentState To)> s_validTransitions =
    [
        // Offline -> Available
        (AgentState.Offline, AgentState.Available),

        // Available -> many
        (AgentState.Available, AgentState.Busy),
        (AgentState.Available, AgentState.Break),
        (AgentState.Available, AgentState.Lunch),
        (AgentState.Available, AgentState.Training),
        (AgentState.Available, AgentState.DND),
        (AgentState.Available, AgentState.Offline),

        // Busy -> Available, ACW
        (AgentState.Busy, AgentState.Available),
        (AgentState.Busy, AgentState.ACW),

        // ACW -> Available, Break, Offline
        (AgentState.ACW, AgentState.Available),
        (AgentState.ACW, AgentState.Break),
        (AgentState.ACW, AgentState.Offline),

        // Breaks -> Available, Offline
        (AgentState.Break, AgentState.Available),
        (AgentState.Break, AgentState.Offline),
        (AgentState.Lunch, AgentState.Available),
        (AgentState.Lunch, AgentState.Offline),
        (AgentState.Training, AgentState.Available),
        (AgentState.Training, AgentState.Offline),
        (AgentState.DND, AgentState.Available),
        (AgentState.DND, AgentState.Offline),
    ];

    private static readonly HashSet<AgentState> s_routableStates =
    [
        AgentState.Available,
        AgentState.Busy,
    ];

    public static bool CanTransition(AgentState from, AgentState to) =>
        s_validTransitions.Contains((from, to));

    public static bool IsRoutable(AgentState state) =>
        s_routableStates.Contains(state);

    public static void EnsureTransition(AgentState from, AgentState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"Invalid agent state transition: {from} -> {to}");
        }
    }
}
