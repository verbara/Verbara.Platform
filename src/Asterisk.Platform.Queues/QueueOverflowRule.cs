using Asterisk.Platform.Core;

namespace Asterisk.Platform.Queues;

public sealed class QueueOverflowRule
{
    public required EntityId OverflowQueueId { get; init; }
    public required int OverflowAfterSeconds { get; init; }
}
