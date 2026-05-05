using Verbara.Platform.Core;

namespace Verbara.Platform.Routing.Inbound;

public sealed record RouteResult(
    EntityId QueueId,
    MessagePriority Priority,
    EntityId? PreferredAgentId,
    IReadOnlyDictionary<string, string>? Metadata);
