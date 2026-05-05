using Verbara.Platform.Core;

namespace Verbara.Platform.Flows;

/// <summary>
/// Persistent runtime state for a single flow execution instance.
/// </summary>
public sealed class FlowExecution : ITenantScoped
{
    public required EntityId ExecutionId { get; init; }
    public required EntityId FlowId { get; init; }
    public required int FlowVersion { get; init; }
    public required TenantId TenantId { get; init; }
    public required EntityId ConversationId { get; init; }
    public required EntityId CurrentNodeId { get; set; }
    public FlowExecutionStatus Status { get; set; } = FlowExecutionStatus.Running;
    public Dictionary<string, string> Variables { get; init; } = new();
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int StepCount { get; set; }
}

/// <summary>
/// Lifecycle status of a flow execution.
/// </summary>
public enum FlowExecutionStatus
{
    Running = 0,
    WaitingForInput = 1,
    Completed = 2,
    Failed = 3,
    HandedOff = 4,
}
