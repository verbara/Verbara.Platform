using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public sealed class QueueOverflowRule
{
    public required EntityId OverflowQueueId { get; init; }
    public required int OverflowAfterSeconds { get; init; }
}
