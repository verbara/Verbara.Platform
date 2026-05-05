namespace Verbara.Platform.Conversations;

public sealed record MessageEnvelope(IReadOnlyList<MessageBlock> Blocks);
